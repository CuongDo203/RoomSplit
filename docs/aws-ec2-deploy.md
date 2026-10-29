# Giai đoạn 2: deploy RoomSplit lên EC2, truy cập từ Internet

Tài liệu phương án, tiếp theo [aws-lab-architecture.md](aws-lab-architecture.md) và [aws-lab-walkthrough.md](aws-lab-walkthrough.md). Mục tiêu ban đầu của giai đoạn 2 chỉ là "thấy được public khác private". Bản này nâng lên thành **chạy RoomSplit thật trên EC2 và gọi được API từ Internet**. Bài thí nghiệm public/private vẫn giữ, vì máy chạy API chính là máy public.

> ✅ **Đã chốt 29/10/2026: E1 + M1 + I3 + A1 + P1** (đúng đề xuất ở mục 6). Các bước chi tiết: [aws-lab-walkthrough.md](aws-lab-walkthrough.md), giai đoạn 2. Giá là mức tham khảo `us-east-1`.

## Mục lục
1. [Kiểm tra giai đoạn 1 (29/10/2026)](#1-kiểm-tra-giai-đoạn-1-29102026)
2. [Yêu cầu và giới hạn](#2-yêu-cầu-và-giới-hạn)
3. [Những gì đã có sẵn trong dự án](#3-những-gì-đã-có-sẵn-trong-dự-án)
4. [Phân tích: cần quyết định những gì](#4-phân-tích-cần-quyết-định-những-gì)
5. [Các phương án](#5-các-phương-án)
6. [Đề xuất](#6-đề-xuất)
7. [Kiến trúc sau khi deploy (theo đề xuất)](#7-kiến-trúc-sau-khi-deploy-theo-đề-xuất)
8. [Các bước làm (tóm tắt)](#8-các-bước-làm-tóm-tắt)
9. [Chi phí](#9-chi-phí)

---

## 1. Kiểm tra giai đoạn 1 (29/10/2026)

Claude kiểm tra bằng CLI (chỉ đọc). Phần **đã đúng**:

| Thành phần | Trạng thái |
|---|---|
| VPC `10.20.0.0/16`, DNS hostnames bật | ✅ |
| 6 subnet đúng tên, AZ, CIDR, auto-assign public IP tắt | ✅ |
| IGW attached | ✅ |
| `rt-public` (`0.0.0.0/0 → igw`, gắn public-a/b), `rt-app-a`, `rt-app-b`, `rt-data` | ✅ |
| Gateway Endpoint S3 gắn `rt-app-a`, `rt-app-b` | ✅ |
| SG `eice`, `test-public`, `app`, `db`, `alb` đã tạo | ✅ |

Phần **còn thiếu**, làm tiếp theo [aws-lab-walkthrough.md](aws-lab-walkthrough.md) mục 1.6–1.7:

| Thiếu | Cần làm |
|---|---|
| Main route table chưa có Name tag | Đặt tên `roomsplit-lab-rt-main` |
| SG `eice` thiếu outbound 22 → `test-public` | Thêm (hiện chỉ có 22 → `app`) |
| SG `app` chưa có inbound | 8080 từ `alb`, 22 từ `eice` |
| SG `db` chưa có inbound | 3306 từ `app` |
| SG `alb` chưa có rule | 80, 443 từ `0.0.0.0/0`; outbound 8080 → `app` |
| SG `redis` chưa tạo | Tạo, inbound 6379 từ `app`, xóa outbound |
| NACL `nacl-data` chưa tạo | Cả 6 subnet đang dùng `nacl-default` |

Những chỗ thiếu này **không chặn** giai đoạn 2 (deploy chỉ dùng public-b, `eice` và SG mới ở mục 7). Nên làm cho xong để giai đoạn 4 không phải quay lại.

---

## 2. Yêu cầu và giới hạn

**Yêu cầu:**
- Từ trình duyệt gọi được `http://<địa chỉ>/swagger` và `GET /api/v1/rooms`.
- Có đủ MySQL và Redis để API chạy như ở máy dev (`/health/ready` trả `Healthy`).
- Dữ liệu còn nguyên khi stop/start máy.

**Giới hạn:**
- **Free plan:** chỉ chạy được các instance type thuộc diện Free Tier (theo tài liệu AWS hiện tại: `t3.micro`, `t3.small`, `t4g.micro`, `t4g.small`, `c7i-flex.large`, `m7i-flex.large`; kiểm tra lại khi launch, Console đánh dấu "Free tier eligible"). Mọi chi phí trừ vào credit.
- **Quyền:** `dev-readonly` có `ec2:*` nhưng chỉ **đọc** IAM, không có quyền ECR. Việc gì cần tạo IAM role thì phải làm bằng root.
- **API chưa có xác thực** (CLAUDE.md: auth nằm ngoài phạm vi MVP). Mở cho cả Internet nghĩa là **ai cũng thêm/xóa được dữ liệu**. Đây là lý do cho phương án ở mục 5.4.
- Kiến trúc đã chốt: máy chạy ở **public-b**, SSH qua **EICE**.

---

## 3. Những gì đã có sẵn trong dự án

| Có sẵn | Dùng lại thế nào |
|---|---|
| `Dockerfile` multi-stage, image chạy cổng 8080, non-root | Build một lần trên máy dev (x86_64), đưa image lên EC2 |
| `docker-compose.yml` + `docker-compose.prod.yml` | Chạy nguyên bộ `mysql + redis + api` trên EC2, có giới hạn RAM, log rotation, Redis có mật khẩu, MySQL không mở cổng |
| `envs/staging.env.example` | Thêm `envs/aws-lab.env.example` cùng cấu trúc |
| `appsettings.Staging.json` (`Swagger:Enabled = true`) | Chạy với `ASPNETCORE_ENVIRONMENT=Staging`, Swagger bật |
| Migration bằng script idempotent ([docker-multi-env.md](docker-multi-env.md) mục 7) | Chép `migrate.sql` lên máy, đưa vào container `mysql` |
| `/health/live`, `/health/ready` | Kiểm tra sau deploy |

Không cần sửa code C#. `UseHttpsRedirection()` sẽ ghi một cảnh báo "Failed to determine the https port" vì container chỉ nghe HTTP; cảnh báo này vô hại và đã có từ khi chạy compose staging.

---

## 4. Phân tích: cần quyết định những gì

| # | Quyết định | Ảnh hưởng |
|---|---|---|
| D1 | Đường vào từ Internet: EC2 có IP public trực tiếp, hay ALB đứng trước EC2 private | Chi phí (ALB + NAT khoảng 50 USD/tháng), độ giống production |
| D2 | MySQL: container trên cùng EC2 hay RDS | Chi phí, có dùng tới tầng data / SG `db` / NACL hay chưa |
| D3 | Đưa image lên EC2 bằng cách nào | Cần thêm dịch vụ/IAM hay không; có giữ nguyên tắc "build một lần" |
| D4 | Ai được gọi API | An toàn dữ liệu, vì API chưa có xác thực |
| D5 | HTTP hay HTTPS | Cần tên miền và certificate hay không |
| D6 | Instance type, AMI | RAM đủ cho 3 container; cách cài Docker |

**Edge case:**
- **RAM:** `docker-compose.prod.yml` giới hạn mysql 1 GB + api 512 MB + redis 128 MB, cộng lại khoảng 1,6 GB. `t3.micro` (1 GB) sẽ bị OOM, hoặc phải hạ giới hạn và thêm swap. `t3.small` (2 GB) đủ.
- **IP public thay đổi** mỗi lần stop/start (IP tự gán). Elastic IP giữ được IP nhưng **vẫn tính tiền khi máy đã stop**.
- **Kiến trúc CPU:** `t3` là x86_64, khớp với image build trên Windows. `t4g` là ARM, phải build multi-arch.
- **Dữ liệu** nằm trong volume Docker trên ổ EBS gốc: stop/start không mất, **terminate thì mất** (Delete on termination).
- **EICE lần đầu:** AWS cần tạo service-linked role `AWSServiceRoleForEC2InstanceConnect`. `AmazonEC2FullAccess` không cho tạo role này, nên lần đầu tạo EICE có thể phải dùng root.

---

## 5. Các phương án

### 5.1 D1: Đường vào từ Internet

```
E1. EC2 public                              E2. ALB + EC2 private
Internet                                    Internet
   │ :80                                       │ :80
  IGW                                         IGW
   │                                           │
public-b: EC2 (IP public)                   public-a/b: ALB ── NAT (để EC2 kéo image)
   docker: api + mysql + redis                 │ :8080
                                            app-a: EC2 (không IP public)
```

| | E1. EC2 public, IP public | E2. ALB + EC2 private + NAT |
|---|---|---|
| Học được | IGW, public IP, SG mở cổng ra Internet | ALB, target group, health check, NAT thường trực |
| Chi phí thêm | IPv4 0,005 USD/giờ | ALB khoảng 16 USD/tháng + NAT khoảng 33 USD/tháng |
| An toàn | EC2 lộ trực tiếp; SG chỉ mở 80 | EC2 không lộ |
| Khớp kiến trúc đã chốt | Đúng chỗ của `test-public` | Đúng hình production, nhưng N1 chỉ cho NAT chạy tạm |

### 5.2 D2: Database

| | M1. MySQL container trên cùng EC2 | M2. RDS MySQL `db.t3.micro`/`db.t4g.micro` ở data-a/b |
|---|---|---|
| Thay đổi | Không: dùng nguyên compose | Bỏ service `mysql` khỏi compose, connection string trỏ endpoint RDS |
| Học được | Docker trên server | DB subnet group (2 AZ), SG `db`, `nacl-data`, backup tự động |
| Chi phí | 0 thêm | Khoảng 12–15 USD/tháng + storage |
| Rủi ro | Mất máy là mất DB (lab chấp nhận được) | |

### 5.3 D3: Đưa image lên EC2

| | I1. Build ngay trên EC2 | I2. ECR + IAM role cho EC2 | I3. `docker save` → copy qua EICE → `docker load` |
|---|---|---|---|
| Cách làm | Copy mã nguồn lên máy, `docker build` | Build ở máy dev, push ECR; EC2 pull bằng instance profile | Build ở máy dev, xuất file `.tar.gz` (khoảng 100 MB), `scp` qua tunnel EICE |
| Dịch vụ/quyền thêm | Không | ECR repo, IAM role, `iam:PassRole` (cần root) | Không |
| "Build một lần" | ❌ Build lại trên server; SDK .NET ngốn RAM máy 2 GB | ✅ | ✅ Cùng image đã chạy ở staging local |
| Học được | Ít | Instance profile, credential tạm (bài IAM) | SSH tunnel qua EICE |
| Khi đổi code | Pull mã + build lại | `docker push` + `docker pull` | Copy lại file tar |

### 5.4 D4: Ai được gọi API

| | A1. Chỉ IP của bạn (SG source = My IP) | A2. Cả Internet (`0.0.0.0/0`) |
|---|---|---|
| Ưu | Bot quét cổng không thấy; dữ liệu an toàn | Đưa link cho người khác dùng thử được |
| Nhược | IP nhà/4G đổi thì phải sửa rule | API không có auth: ai cũng sửa/xóa được dữ liệu; bot quét sẽ tìm thấy Swagger trong vài giờ |

Cả hai đều là "truy cập từ Internet": request đi qua Internet → IGW → EC2. Chỉ khác là SG cho ai đi qua.

### 5.5 D5: HTTP hay HTTPS

| | P1. HTTP cổng 80 | P2. Caddy reverse proxy + Let's Encrypt, tên miền `<ip>.sslip.io` |
|---|---|---|
| Ưu | Không thêm gì | HTTPS thật, miễn phí, không cần mua tên miền |
| Nhược | Không mã hóa | Thêm 1 container; IP đổi thì tên miền đổi và phải xin certificate lại; cần thêm `ForwardedHeaders` |

### 5.6 D6: Instance và AMI

| | Chọn | Lý do |
|---|---|---|
| Type | `t3.small` (2 vCPU, 2 GB) | Đủ RAM cho giới hạn hiện có của compose; Free Tier eligible; x86_64 |
| AMI | Amazon Linux 2023 (x86_64) | AMI của AWS, có sẵn EC2 Instance Connect; cài Docker bằng `dnf`, Compose plugin tải từ GitHub trong user data |
| Ổ đĩa | gp3 20 GB | Đủ cho image + MySQL lab |
| IP public | Tự gán (không dùng EIP) | EIP tính tiền cả khi stop |
| Key pair | `roomsplit-lab-key` (ED25519), lưu ở `~/.ssh`, **không** để trong repo | Dùng cho `scp`/`ssh` qua tunnel EICE |

---

## 6. Đề xuất

**E1 + M1 + I3 + A1 + P1**, instance như mục 5.6.

| Quyết định | Chọn | Lý do |
|---|---|---|
| Đường vào | **E1** | Rẻ nhất, đúng vị trí `test-public` đã thiết kế. ALB + NAT để dành cho một giai đoạn sau khi cần học ALB |
| Database | **M1** | Dùng nguyên `docker-compose.prod.yml` đã kiểm chứng ở local; không thêm chi phí. RDS (M2) là bước tiếp theo tự nhiên vì tầng data đã sẵn |
| Image | **I3** | Không cần IAM hay dịch vụ mới (dev-readonly làm được hết), giữ nguyên tắc build một lần. Chuyển sang ECR (I2) khi làm CI/CD (P2 trong CLAUDE.md) |
| Ai được gọi | **A1** | API chưa có auth. Khi cần demo cho người khác thì mở `0.0.0.0/0` tạm vài giờ rồi đóng |
| Giao thức | **P1** | Đủ cho lab; HTTPS gắn với tên miền thật làm sau |

---

## 7. Kiến trúc sau khi deploy (đã chốt)

```
Bạn (IP nhà) ── HTTP :80 ──► IGW ──► public-b: EC2 roomsplit-lab-web (t3.small, IP public)
                                        SG roomsplit-lab-web
                                        docker compose (project roomsplit-lab):
                                          api   :8080 ◄── host :80
                                          mysql :3306 (chỉ trong mạng compose)
                                          redis :6379 (chỉ trong mạng compose)
Bạn ── aws ec2-instance-connect (API AWS) ──► EICE (app-a) ── SSH :22 ──► EC2
```

**SG mới `roomsplit-lab-web`** (thay cho `test-public` ở máy này):

| Chiều | Port | Source / Destination | Lý do |
|---|---|---|---|
| Inbound | 80 | **My IP** (`x.x.x.x/32`) | Gọi API (A1) |
| Inbound | 22 | `roomsplit-lab-eice` | SSH/scp qua EICE |
| Outbound | All | `0.0.0.0/0` | `dnf install`, tải Docker Compose, pull image `mysql`/`redis` từ Docker Hub |

SG `eice` thêm outbound 22 → `roomsplit-lab-web`.

**File mới trong repo:** `envs/aws-lab.env.example` (commit) và `envs/aws-lab.env` (git-ignored, giữ ở máy dev, copy lên EC2):

```
COMPOSE_PROJECT_NAME=roomsplit-lab
ASPNETCORE_ENVIRONMENT=Staging
API_TAG=<git describe>
API_PORT=80
MYSQL_ROOT_PASSWORD=
MYSQL_USER=roomsplit_app
MYSQL_PASSWORD=
REDIS_PASSWORD=
```

---

## 8. Các bước làm (tóm tắt)

Chi tiết (Console + lệnh): [aws-lab-walkthrough.md](aws-lab-walkthrough.md), giai đoạn 2. Ngoài bảng dưới còn bước 2.1: dùng root cấp quyền `ec2-instance-connect:OpenTunnel`/`SendSSHPublicKey` cho group `developers` và tạo EICE lần đầu (`dev-readonly` không có các quyền này).

| Bước | Làm gì | Kiểm chứng |
|---|---|---|
| 2.1 | Tạo SG `web`, sửa SG `eice`; tạo EICE trong app-a (lần đầu có thể cần root) | EICE ở trạng thái `create-complete` |
| 2.2 | Launch `t3.small` AL2023 ở **public-b**, bật auto-assign public IP, SG `web`, key pair, user data cài Docker + Compose | `aws ec2-instance-connect ssh --instance-id i-… --connection-type eice` vào được; `docker compose version` chạy |
| 2.3 | Máy dev: `docker build`, `docker save \| gzip`, `dotnet ef migrations script --idempotent` | Có `roomsplit-api-<tag>.tar.gz`, `migrate.sql` |
| 2.4 | Mở tunnel EICE, `scp` image + 2 file compose + `aws-lab.env` + `migrate.sql` | File có trên máy |
| 2.5 | Trên EC2: `docker load`, `docker compose ... up -d`, đưa `migrate.sql` vào `mysql` | `docker compose ps` cả 3 healthy/running |
| 2.6 | Từ trình duyệt: `http://<public-ip>/swagger`, `/health/ready`; tạo một room | Hoạt động. Thử từ 4G (IP khác) thì **timeout** vì SG chỉ cho IP của bạn |
| 2.7 | Thí nghiệm: tắt auto IP / stop-start máy | IP public đổi; dữ liệu vẫn còn |
| 2.8 | Kết thúc buổi: **Stop** instance (không terminate) | Chỉ còn tính tiền EBS |

---

## 9. Chi phí

Giá tham khảo `us-east-1`, trừ vào credit Free plan:

| Thành phần | Đơn giá | Chạy 24/7 một tháng | Chỉ bật khi học (khoảng 40 giờ/tháng) |
|---|---|---|---|
| EC2 `t3.small` | khoảng 0,0208 USD/giờ | khoảng 15 USD | khoảng 0,8 USD |
| Public IPv4 | 0,005 USD/giờ | khoảng 3,6 USD | khoảng 0,2 USD |
| EBS gp3 20 GB | khoảng 0,08 USD/GB-tháng | 1,6 USD | 1,6 USD (tính cả khi stop) |
| EICE, data transfer vào | 0 | 0 | 0 |
| Data transfer ra Internet | 100 GB/tháng đầu miễn phí (toàn tài khoản) | khoảng 0 | 0 |
| **Tổng** | | **khoảng 20 USD** | **khoảng 2,6 USD** |

Budget 5 USD ở giai đoạn 0 sẽ báo động nếu quên stop máy khoảng một tuần.
