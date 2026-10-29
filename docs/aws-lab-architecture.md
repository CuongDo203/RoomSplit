# Kiến trúc AWS dự kiến cho RoomSplit (lab trên tài khoản free)

Tài liệu phương án, tiếp theo [aws-networking.md](aws-networking.md) (lý thuyết) và [iam-notes.md](iam-notes.md) (quyền truy cập). Bài lý thuyết chỉ có **bản phác thảo** ở mục 16. Bài này biến bản phác thảo đó thành **kế hoạch làm thật** trên một tài khoản AWS free: chọn Region/AZ, chia CIDR, subnet, route table, Internet Gateway, NAT, Security Group, Network ACL, rồi dựng theo từng giai đoạn để kiểm soát chi phí.

> ⚠️ **Chưa tạo tài nguyên nào.** Phương án đã được chốt ở mục 5; sơ đồ trực quan: [aws-lab-architecture.html](aws-lab-architecture.html) (bản online: https://claude.ai/artifact/DHxKH8Yy2BgNkVkgDZoVkB, riêng tư cho chủ tài khoản). Giá ghi trong bài là **mức tham khảo** cho `us-east-1`, có thể đã thay đổi: kiểm tra trang AWS Pricing trước khi tạo tài nguyên tính tiền.

## Mục lục
1. [Mục tiêu và giới hạn](#1-mục-tiêu-và-giới-hạn)
2. [Tài khoản free: hai loại cần phân biệt](#2-tài-khoản-free-hai-loại-cần-phân-biệt)
3. [Phân tích: cần quyết định những gì](#3-phân-tích-cần-quyết-định-những-gì)
4. [Các phương án](#4-các-phương-án)
5. [Đề xuất](#5-đề-xuất)
6. [Kiến trúc chi tiết (theo đề xuất)](#6-kiến-trúc-chi-tiết-theo-đề-xuất)
7. [Lộ trình làm thật theo giai đoạn](#7-lộ-trình-làm-thật-theo-giai-đoạn)
8. [Bảng ghi lại tài nguyên đã tạo](#8-bảng-ghi-lại-tài-nguyên-đã-tạo)
9. [Chi phí dự kiến](#9-chi-phí-dự-kiến)
10. [Dọn dẹp](#10-dọn-dẹp)

---

## 1. Mục tiêu và giới hạn

**Mục tiêu học** (theo thứ tự):

1. Region và Availability Zone: chọn region, biết AZ name khác AZ ID.
2. VPC là mạng riêng, chia dải bằng CIDR.
3. Subnet public và private **giống hệt nhau lúc tạo**; khác nhau duy nhất ở **route table** có trỏ `0.0.0.0/0` tới **Internet Gateway** hay không.
4. NAT Gateway: private subnet đi ra Internet được nhưng Internet không đi vào được.
5. Security Group (stateful, gắn vào ENI) và Network ACL (stateless, gắn vào subnet).
6. Ghi lại toàn bộ CIDR, subnet, route table, IGW đã tạo (mục 8).

**Giới hạn:**

- Tài khoản free: phần **mạng** phải miễn phí; thứ gì tính tiền theo giờ (NAT, EC2, public IPv4) chỉ bật **ngắn hạn** rồi xóa.
- Chưa deploy RoomSplit (ECS, RDS, ElastiCache, ALB). Thiết kế mạng ở đây **chừa sẵn chỗ** cho chúng để sau này không phải chia lại subnet.
- Không đụng **default VPC** (`172.31.0.0/16`): giữ nguyên, tạo VPC mới.

---

## 2. Tài khoản free: hai loại cần phân biệt

AWS đổi chương trình Free Tier từ **15/07/2025**. Cách tính "miễn phí" phụ thuộc vào ngày tạo tài khoản:

| | Tài khoản tạo **trước** 15/07/2025 | Tài khoản tạo **từ** 15/07/2025 |
|---|---|---|
| Mô hình | Free Tier 12 tháng: hạn mức theo giờ/GB | **Free plan**: nhận credit (khoảng 100 USD lúc đăng ký, thêm tối đa 100 USD khi làm các hoạt động khám phá), dùng trong 6 tháng |
| EC2 | 750 giờ/tháng `t2.micro`/`t3.micro` (kèm 750 giờ public IPv4) | Trừ vào credit |
| RDS | 750 giờ/tháng `db.t3.micro`/`db.t4g.micro` Single-AZ, 20 GB | Trừ vào credit |
| NAT Gateway | **Không miễn phí** | Trừ vào credit |
| Hết hạn mức | Bị tính tiền vào thẻ | Free plan **không trừ thẻ**; hết credit hoặc hết 6 tháng thì tài khoản bị đóng nếu không nâng cấp lên Paid plan |
| Một số dịch vụ | Dùng được hết | Free plan có thể **chặn** một số dịch vụ/tính năng |

Dù là loại nào, những thứ sau **luôn miễn phí**: VPC, subnet, route table, Internet Gateway, Security Group, Network ACL, Gateway Endpoint S3, EC2 Instance Connect Endpoint.

> ✅ **Đã xác nhận (28/10/2026):** tài khoản lab tạo **sau** 15/07/2025, tức là **Free plan**. EC2, NAT, public IPv4 đều trừ vào credit; thẻ không bị trừ tiền nhưng hết credit thì tài khoản bị đóng. Các bước làm cụ thể: [aws-lab-walkthrough.md](aws-lab-walkthrough.md).

Việc bắt buộc làm trước, cho cả hai loại: bật **MFA cho root**, dùng IAM user/role thay root ([iam-notes.md](iam-notes.md) mục 1, 7), tạo **AWS Budgets** cảnh báo ở mức 5 USD.

---

## 3. Phân tích: cần quyết định những gì

| # | Quyết định | Ảnh hưởng |
|---|---|---|
| D1 | **Region** | Độ trễ cho người dùng Việt Nam, giá, dịch vụ có sẵn. Mọi tài nguyên sau đều thuộc region này |
| D2 | **Số AZ và số tầng subnet** | Khả năng chịu lỗi; ALB và RDS **bắt buộc** subnet ở ít nhất 2 AZ (kể cả RDS Single-AZ cần DB subnet group 2 AZ) |
| D3 | **CIDR của VPC và subnet** | Gần như **không đổi được** sau khi tạo (chỉ thêm được CIDR phụ). Chồng dải với VPC/mạng khác thì sau này không peering/VPN được |
| D4 | **Cách private subnet đi ra Internet** | NAT Gateway (thật, tốn tiền), NAT instance (rẻ, tự vận hành) hoặc không có |
| D5 | **Network ACL** | Giữ mặc định (cho tất cả) hay viết NACL riêng cho tầng data |
| D6 | **Cách vào máy trong private subnet** | Bastion (SSH qua máy public), SSM Session Manager, EC2 Instance Connect Endpoint |
| D7 | **Công cụ dựng** | Console, CLI, hay IaC (CloudFormation/Terraform) |

Edge case cần nhớ:

- Mỗi subnet mất **5 IP** cho AWS (mục 4.1 bài lý thuyết): `/24` chỉ dùng được 251.
- Subnet chưa gắn route table nào sẽ dùng **main route table**. Nếu lỡ thêm route IGW vào main thì mọi subnet mới đều thành public. Quy tắc: **main chỉ có `local`**, các subnet đều gắn route table tường minh.
- `us-east-1a` của tài khoản bạn có thể là AZ vật lý khác với người khác: ghi lại **AZ ID** (`use1-az1`, …).
- NAT Gateway phải nằm trong **public subnet** và cần một **Elastic IP**.
- NACL stateless: cho phép 3306 chiều vào mà quên **cổng ephemeral 1024–65535** chiều ra thì kết nối sẽ **timeout**.

---

## 4. Các phương án

### 4.1 D1: Region

| Phương án | Ưu | Nhược |
|---|---|---|
| **R1. `ap-southeast-1` Singapore** | Độ trễ khoảng 30–50 ms từ VN; đủ dịch vụ, 3 AZ; khớp với cấu hình `aws configure` đã làm ở bài IAM | Giá cao hơn `us-east-1` khoảng 10–30% |
| R2. `ap-southeast-7` Thái Lan | Gần hơn | Region mới (opt-in), ít dịch vụ hơn, ít tài liệu |
| R3. `us-east-1` Virginia | Rẻ nhất, tính năng mới thường có ở đây trước | Độ trễ 200+ ms; khác region với những gì đã cấu hình |

### 4.2 D2 + D4: Topology và đường ra Internet

**Phương án A: Tối giản, 1 AZ, 2 subnet**

```
VPC 10.20.0.0/16 (chỉ AZ a)
  public-a 10.20.0.0/24  → IGW
  private-a 10.20.10.0/24 → (không có gì)
```

- Ưu: đúng như lab ở mục 17 bài lý thuyết, nhanh, miễn phí hoàn toàn.
- Nhược: không tạo được ALB hay RDS sau này (cần 2 AZ); phải chia lại mạng, tức là làm lại từ đầu. Không có NAT nên không học được NAT.

**Phương án B: 2 AZ × 3 tầng (public / app / data), NAT bật tạm thời**

```
VPC 10.20.0.0/16, AZ a + AZ b
  public-a, public-b  → IGW                 (ALB, NAT)
  app-a,    app-b     → NAT (khi bật)       (EC2/ECS chạy API)
  data-a,   data-b    → chỉ local           (RDS, Redis)
```

- Ưu: **giống hình dạng production** (mục 16 bài lý thuyết), dùng thẳng cho ALB/RDS sau này. Toàn bộ phần mạng **miễn phí**. NAT Gateway chỉ bật vài giờ khi thử rồi xóa (khoảng 0,045 USD/giờ ở `us-east-1`).
- Nhược: nhiều tài nguyên hơn (6 subnet, 4 route table), mất thời gian dựng hơn A.

**Phương án C: 3 AZ × 3 tầng, NAT mỗi AZ chạy thường trực**

- Ưu: chuẩn production đầy đủ, chịu được sự cố 1 AZ hoàn toàn.
- Nhược: 3 NAT khoảng 130 USD/tháng trở lên; 9 subnet; quá mức cần cho việc học và cho quy mô RoomSplit.

**Biến thể cho D4 (dùng chung với B):**

| | N1. NAT Gateway, bật tạm | N2. NAT instance (EC2 tự làm NAT) | N3. Không NAT |
|---|---|---|---|
| Học được | NAT "thật" mà production dùng | Bản chất NAT: route tới **ENI**, tắt **source/dest check**, iptables | Chỉ thấy private subnet không ra được Internet |
| Chi phí | Khoảng 0,045 USD/giờ + 0,045 USD/GB (us-east-1) + EIP | Free tier EC2 (tài khoản cũ) hoặc vài cent/giờ | 0 |
| Vận hành | AWS lo, tự mở rộng | Tự vá, tự lo khi máy chết | — |
| Rủi ro | **Quên xóa** thì khoảng 33 USD/tháng | Cấu hình sai thì mất kết nối | — |

### 4.3 D5: Network ACL

| Phương án | Ưu | Nhược |
|---|---|---|
| L1. Giữ NACL mặc định cho mọi subnet | Không thể cấu hình sai; SG đã đủ an toàn | Không thấy được tính **stateless** |
| **L2. Mặc định cho public/app, NACL riêng `nacl-data` cho tầng data** | Học stateless trên đúng chỗ có ý nghĩa (data chỉ nhận kết nối từ app); thêm một lớp phòng thủ | Thêm rule phải nhớ cổng ephemeral |
| L3. NACL riêng cho cả 3 tầng | Chặt nhất | Dễ tự khóa mình, khó gỡ lỗi; ít giá trị thêm so với SG |

### 4.4 D6: Vào máy private

| Phương án | Ưu | Nhược |
|---|---|---|
| S1. Bastion host (EC2 public + SSH) | Kinh điển, dễ hiểu | Thêm một máy public, phải giữ key, mở cổng 22 |
| S2. SSM Session Manager | Không mở cổng nào, có log | Máy private cần đi ra endpoint SSM: phải qua **NAT** hoặc 3 **Interface Endpoint** (tốn tiền theo giờ) |
| **S3. EC2 Instance Connect Endpoint (EICE)** | **Miễn phí**, không cần NAT hay public IP, SSH qua console hoặc `aws ec2-instance-connect ssh` | Mỗi VPC một endpoint; phải chỉnh SG cho cổng 22 từ SG của EICE |

### 4.5 D7: Công cụ

| Phương án | Ưu | Nhược |
|---|---|---|
| **T1. Console trước, CLI để kiểm tra** | Nhìn thấy từng khối, có **Resource map** vẽ sẵn sơ đồ, hợp với mục tiêu học | Bấm tay, khó lặp lại, dễ sót khi dọn |
| T2. Script CLI (PowerShell) trong repo | Lặp lại được, ghi lại được | Phải tự viết phần xóa theo thứ tự ngược |
| T3. CloudFormation/Terraform | Chuẩn thực tế; **xóa stack là dọn sạch** | Phải học thêm một công cụ trước khi thấy kết quả |

---

## 5. Quyết định đã chốt

Chốt ngày 28/10/2026: **R3 + B (với N1) + L2 + S3 + T1**, chuyển sang T3 sau khi đã hiểu.

| Quyết định | Chọn | Lý do |
|---|---|---|
| Region | **R3** `us-east-1` (N. Virginia) | Rẻ nhất (NAT, EC2, data transfer đều thấp hơn Singapore). Độ trễ 200+ ms từ VN không quan trọng với lab |
| Topology | **B**: 2 AZ × 3 tầng, dùng `us-east-1a` + `us-east-1b` | Phần mạng miễn phí nên không có lý do làm nhỏ hơn; dùng lại được cho ALB/RDS mà không phải chia lại subnet |
| NAT | **N1**, bật 1–2 giờ rồi xóa | Học đúng thứ dùng ở production; tốn khoảng 0,1 USD nếu xóa đúng hạn. Thích tìm hiểu sâu thì làm thêm N2 sau |
| NACL | **L2** | Thấy stateless ở đúng chỗ cần, không làm rối hai tầng còn lại |
| Vào máy private | **S3** EICE | Miễn phí, không phụ thuộc NAT (giai đoạn chưa có NAT vẫn SSH được) |
| Công cụ | **T1** | Lần đầu nên "nhìn tận mắt". Khi xong giai đoạn 3, chuyển mạng sang CloudFormation (bài riêng) |

Lưu ý khi dùng `us-east-1`:

- Profile trên máy đang để region mặc định `ap-southeast-1` (bài IAM). Trên Console nhớ chọn **N. Virginia** ở góc phải trên; với CLI luôn thêm `--region us-east-1` (hoặc `$env:AWS_REGION = "us-east-1"` cho cả phiên). Tạo nhầm region là lỗi phổ biến nhất: tài nguyên "biến mất" vì đang xem region khác.
- `us-east-1` có 6 AZ (`1a`…`1f`). Tránh `us-east-1e`: AZ này cũ và không có nhiều loại instance mới. `1a`/`1b` của tài khoản bạn trỏ tới AZ ID nào thì ghi vào mục 8.

---

## 6. Kiến trúc chi tiết (đã chốt)

### 6.1 Sơ đồ

Bản trực quan (4 luồng traffic, SG, NACL, nút xem theo từng giai đoạn): [aws-lab-architecture.html](aws-lab-architecture.html), mở bằng trình duyệt. Bản ASCII dưới đây để đọc nhanh trong repo.

```
Region us-east-1 · VPC roomsplit-lab-vpc 10.20.0.0/16 · DNS hostnames: on

                               Internet
                                  │
                       igw  roomsplit-lab-igw
                                  │
            AZ a (us-east-1a)     │      AZ b (us-east-1b)
  ┌───────────────────────────────┼───────────────────────────────┐
  │ public-a  10.20.0.0/24        │  public-b  10.20.1.0/24       │  rt-public
  │   NAT GW (EIP)  [GĐ 3]        │   test-public EC2 [GĐ 2]      │  0.0.0.0/0 → igw
  │   (ALB sau này)               │   (ALB sau này)               │
  ├───────────────────────────────┼───────────────────────────────┤
  │ app-a     10.20.10.0/24       │  app-b     10.20.11.0/24      │  rt-app-a / rt-app-b
  │   test-private EC2 [GĐ 2]     │   (ECS/EC2 API sau này)       │  0.0.0.0/0 → nat [GĐ 3]
  │   EICE [GĐ 2]                 │                               │  S3 → vpce-s3
  ├───────────────────────────────┼───────────────────────────────┤
  │ data-a    10.20.20.0/24       │  data-b    10.20.21.0/24      │  rt-data: chỉ local
  │   test-db EC2 [GĐ 4]          │   (RDS standby/subnet group)  │  NACL: nacl-data
  └───────────────────────────────┴───────────────────────────────┘
```

### 6.2 CIDR

| Thành phần | CIDR | Số IP dùng được | Ghi chú |
|---|---|---|---|
| VPC `roomsplit-lab-vpc` | `10.20.0.0/16` | 65.536 | Không trùng default VPC `172.31.0.0/16` hay mạng Docker `172.17–172.31.x` |
| Staging/prod sau này | `10.21.0.0/16`, `10.22.0.0/16` | | Để dành, không chồng nhau, sau này peering được |

Quy ước chia trong VPC (octet thứ 3 cho biết **tầng** và **AZ**):

| Tầng | AZ a | AZ b | AZ c (để dành) | Gom lại |
|---|---|---|---|---|
| public | `10.20.0.0/24` | `10.20.1.0/24` | `10.20.2.0/24` | `10.20.0.0/22` |
| app | `10.20.10.0/24` | `10.20.11.0/24` | `10.20.12.0/24` | `10.20.10.0/23` (a+b) |
| data | `10.20.20.0/24` | `10.20.21.0/24` | `10.20.22.0/24` | `10.20.20.0/23` (a+b) |

`10.20.10.0/23` = `10.20.10.0` → `10.20.11.255`, phủ đúng app-a + app-b. Rule NACL ở mục 6.5 dùng dải gom này, nên chỉ cần **một rule** cho cả hai subnet.

### 6.3 Subnet

| Name tag | AZ | CIDR | Auto-assign public IPv4 | Route table |
|---|---|---|---|---|
| `roomsplit-lab-public-a` | us-east-1a | `10.20.0.0/24` | Tắt (bật theo từng máy khi cần) | `rt-public` |
| `roomsplit-lab-public-b` | us-east-1b | `10.20.1.0/24` | Tắt | `rt-public` |
| `roomsplit-lab-app-a` | us-east-1a | `10.20.10.0/24` | Tắt | `rt-app-a` |
| `roomsplit-lab-app-b` | us-east-1b | `10.20.11.0/24` | Tắt | `rt-app-b` |
| `roomsplit-lab-data-a` | us-east-1a | `10.20.20.0/24` | Tắt | `rt-data` |
| `roomsplit-lab-data-b` | us-east-1b | `10.20.21.0/24` | Tắt | `rt-data` |

Tắt auto-assign public IP kể cả ở public subnet: public IPv4 tính tiền theo giờ. Máy nào cần IP public thì bật lúc tạo máy.

### 6.4 Route table

| Name tag | Gắn vào | Routes |
|---|---|---|
| main (AWS tự tạo, đặt tên `roomsplit-lab-rt-main`) | không gắn tường minh | `10.20.0.0/16 → local` **(giữ nguyên, không thêm gì)** |
| `roomsplit-lab-rt-public` | public-a, public-b | `local`; `0.0.0.0/0 → igw` |
| `roomsplit-lab-rt-app-a` | app-a | `local`; S3 prefix list `→ vpce-s3`; [GĐ 3] `0.0.0.0/0 → nat-a` |
| `roomsplit-lab-rt-app-b` | app-b | `local`; S3 prefix list `→ vpce-s3`; [GĐ 3] `0.0.0.0/0 → nat-a` (lab dùng chung 1 NAT; production sẽ là `nat-b`) |
| `roomsplit-lab-rt-data` | data-a, data-b | `local` |

Lý do tách `rt-app-a` và `rt-app-b` ngay cả khi chung NAT: production mỗi AZ một NAT; đến lúc đó chỉ cần sửa một route, không phải đổi association.

### 6.5 Security Group (stateful)

| SG | Inbound | Outbound | Dùng ở |
|---|---|---|---|
| `roomsplit-lab-eice` | (không) | TCP 22 → `roomsplit-lab-app`, `roomsplit-lab-test-public`, `roomsplit-lab-web` | EICE |
| `roomsplit-lab-test-public` | TCP 22 từ `roomsplit-lab-eice` | Tất cả (mặc định) | EC2 thử ở public-b (đã thay bằng `web` ở giai đoạn 2) |
| `roomsplit-lab-web` | TCP 80 từ **My IP**; TCP 22 từ `roomsplit-lab-eice` | Tất cả (mặc định) | EC2 `roomsplit-lab-web` chạy RoomSplit ([aws-ec2-deploy.md](aws-ec2-deploy.md)) |
| `roomsplit-lab-alb` | TCP 80, 443 từ `0.0.0.0/0` | TCP 8080 → `roomsplit-lab-app` | ALB (sau này) |
| `roomsplit-lab-app` | TCP 8080 từ `roomsplit-lab-alb`; TCP 22 từ `roomsplit-lab-eice` | Tất cả (mặc định; thu hẹp khi deploy thật) | EC2 private / ECS |
| `roomsplit-lab-db` | TCP 3306 từ `roomsplit-lab-app` | (không cần) | RDS |
| `roomsplit-lab-redis` | TCP 6379 từ `roomsplit-lab-app` | (không cần) | ElastiCache |

Không có rule nào dùng IP: tầng sau chỉ tin **SG** của tầng trước. Có thể tạo cả 6 SG ngay từ giai đoạn 1 (miễn phí).

### 6.6 Network ACL (stateless)

`roomsplit-lab-public-*` và `roomsplit-lab-app-*` dùng **default NACL** (allow all). Riêng tầng data:

**`roomsplit-lab-nacl-data`** gắn vào data-a, data-b

| Chiều | Rule # | Giao thức | Cổng | Nguồn/Đích | Hành động |
|---|---|---|---|---|---|
| Inbound | 100 | TCP | 3306 | `10.20.10.0/23` | ALLOW |
| Inbound | 110 | TCP | 6379 | `10.20.10.0/23` | ALLOW |
| Inbound | * | Tất cả | Tất cả | `0.0.0.0/0` | DENY |
| Outbound | 100 | TCP | 1024–65535 | `10.20.10.0/23` | ALLOW (gói **trả lời** về app) |
| Outbound | * | Tất cả | Tất cả | `0.0.0.0/0` | DENY |

So sánh: SG `roomsplit-lab-db` chỉ cần **một** rule inbound, gói trả lời tự được cho qua (stateful). NACL cần **cả hai chiều**. Thí nghiệm ở giai đoạn 4: xóa outbound rule 100 rồi kết nối từ app sẽ thấy **timeout**, không phải refused.

### 6.7 Thành phần khác

| Thành phần | Có trong lab | Ghi chú |
|---|---|---|
| Internet Gateway `roomsplit-lab-igw` | Có, GĐ 1 | Mỗi VPC tối đa 1 IGW |
| Gateway Endpoint S3 `roomsplit-lab-vpce-s3` | Có, GĐ 1 | Miễn phí, gắn `rt-app-a`, `rt-app-b` |
| EC2 Instance Connect Endpoint | Có, GĐ 2 | Đặt ở app-a, SG `roomsplit-lab-eice` |
| NAT Gateway `roomsplit-lab-nat-a` + EIP | Có, GĐ 3, **xóa ngay sau thí nghiệm** | Đặt ở public-a |
| ALB, ECS, RDS, ElastiCache | Không | Bài deploy riêng; mạng đã chừa chỗ |

---

## 7. Lộ trình làm thật theo giai đoạn

| GĐ | Làm gì | Kiểm chứng | Chi phí |
|---|---|---|---|
| **0. Chuẩn bị** | MFA root, IAM admin/role, Budgets 5 USD, chọn region `us-east-1`, ghi AZ ID | `aws sts get-caller-identity`; `aws ec2 describe-availability-zones --query "AvailabilityZones[].[ZoneName,ZoneId]"` | 0 |
| **1. Mạng** | VPC, 6 subnet, IGW, 4 route table, 6 SG, `nacl-data`, Gateway Endpoint S3 | Console **Resource map**: public đi tới IGW, app/data thì không; điền bảng mục 8 | **0** |
| **2. Thấy public vs private** | EC2 `t3.micro` (Amazon Linux 2023) ở **public-b** có public IP; EC2 ở app-a không có public IP; EICE | SSH qua EICE vào cả hai; `curl -m 5 https://checkip.amazonaws.com`: máy public trả về IP của nó, máy private **timeout** | EC2 + public IPv4 vài cent/giờ (tài khoản cũ: nằm trong free tier) |
| **3. NAT** | EIP + NAT Gateway ở public-a; thêm `0.0.0.0/0 → nat` vào `rt-app-a/b` | Máy private `curl` được, IP trả về **là EIP của NAT**; từ ngoài vẫn không vào được máy private | Khoảng 0,06 USD/giờ, **xóa NAT + nhả EIP ngay** |
| **4. Stateful vs stateless** | Đặt tạm một EC2 vào data-a (SG `db`), **user data** chạy `python3 -m http.server 3306` lúc khởi động (data không có Internet để cài gói, NACL chặn SSH nên không cần đăng nhập vào máy này) | Từ test-private: `curl -m 5 http://<ip-test-db>:3306` trả về HTML; xóa outbound rule 100 của NACL thì **timeout**; trả lại rule thì chạy | Như GĐ 2 |
| **5. Dọn hoặc giữ** | Xóa mọi EC2/EICE/NAT/EIP; **giữ** phần mạng của GĐ 1 (miễn phí) cho bài deploy | Billing → Bills không còn dòng tính theo giờ | 0 |

Mỗi giai đoạn xong sẽ cập nhật tài liệu này (bảng mục 8 và ghi chú "đã làm").

---

## 8. Bảng ghi lại tài nguyên đã tạo

Điền ID thật sau khi tạo (giai đoạn 1 kiểm tra bằng CLI ngày 29/10/2026) (ID không phải bí mật, nhưng **không** ghi Account ID, access key hay mật khẩu vào đây).

**Region / AZ**

| AZ name | AZ ID | Dùng cho |
|---|---|---|
| us-east-1a | `use1-az1` | tầng a |
| us-east-1b | `use1-az2` | tầng b |

**VPC, IGW, endpoint**

| Name tag | Loại | CIDR | ID |
|---|---|---|---|
| `roomsplit-lab-vpc` | VPC | `10.20.0.0/16` | `vpc-063d890160ab15270` |
| `roomsplit-lab-igw` | Internet Gateway | — | `igw-0f25c8747457fdaf1` |
| `roomsplit-lab-vpce-s3` | Gateway Endpoint | — | `vpce-00eacba7ee5e1686a` |
| `roomsplit-lab-nat-a` | NAT Gateway (tạm) | EIP: _ | `nat-…` (đã xóa: _) |

**Subnet**

| Name tag | AZ | CIDR | Route table | NACL | ID |
|---|---|---|---|---|---|
| `roomsplit-lab-public-a` | 1a | `10.20.0.0/24` | rt-public | default | `subnet-0f815ccc10dceef48` |
| `roomsplit-lab-public-b` | 1b | `10.20.1.0/24` | rt-public | default | `subnet-0eaddf7404e74750c` |
| `roomsplit-lab-app-a` | 1a | `10.20.10.0/24` | rt-app-a | default | `subnet-070b077daa9aafaed` |
| `roomsplit-lab-app-b` | 1b | `10.20.11.0/24` | rt-app-b | default | `subnet-0b8dec44c3e489708` |
| `roomsplit-lab-data-a` | 1a | `10.20.20.0/24` | rt-data | nacl-data | `subnet-01b03f7adebfb47be` |
| `roomsplit-lab-data-b` | 1b | `10.20.21.0/24` | rt-data | nacl-data | `subnet-0e14e64e406afdceb` |

**Route table**

| Name tag | Routes | Gắn vào | ID |
|---|---|---|---|
| `roomsplit-lab-rt-main` | local | (main) | `rtb-04e2df879a072c456` |
| `roomsplit-lab-rt-public` | local, `0.0.0.0/0 → igw` | public-a, public-b | `rtb-019cee7668443ae4c` |
| `roomsplit-lab-rt-app-a` | local, S3 → vpce | app-a | `rtb-0fcb44c3aed966a72` |
| `roomsplit-lab-rt-app-b` | local, S3 → vpce | app-b | `rtb-05e5ea24746abec34` |
| `roomsplit-lab-rt-data` | local | data-a, data-b | `rtb-099df1608b100a6ec` |

**Security Group / NACL**

| Name | ID |
|---|---|
| `roomsplit-lab-eice` | `sg-02e5d6a6207a17e0e` |
| `roomsplit-lab-test-public` | `sg-0d48ac565d90e24c7` |
| `roomsplit-lab-web` | (giai đoạn 2) |
| `roomsplit-lab-alb` | `sg-0341ffbdfe7bc33dd` |
| `roomsplit-lab-app` | `sg-02c886f416ef8d6fd` |
| `roomsplit-lab-db` | `sg-0fd718830340872e7` |
| `roomsplit-lab-redis` | (chưa tạo) |
| `roomsplit-lab-nacl-data` | (chưa tạo) |

---

## 9. Chi phí dự kiến

Giá tham khảo `us-east-1`, kiểm tra lại trên trang Pricing:

| Thành phần | Đơn giá | Lab dùng | Ước tính |
|---|---|---|---|
| VPC, subnet, RT, IGW, SG, NACL, Gateway Endpoint S3, EICE | 0 | Giữ lâu dài | 0 |
| EC2 `t3.micro` | khoảng 0,0104 USD/giờ | 2 máy × khoảng 3 giờ | khoảng 0,06 USD (free tier cũ: 0) |
| Public IPv4 (EC2, EIP) | 0,005 USD/giờ | 1–2 IP × vài giờ | < 0,05 USD |
| NAT Gateway | khoảng 0,045 USD/giờ + 0,045 USD/GB | 2 giờ, < 1 GB | khoảng 0,1 USD |
| Reachability Analyzer (tùy chọn) | 0,10 USD/lần phân tích | 1–2 lần | 0,2 USD |
| **Tổng nếu dọn đúng hạn** | | | **< 1 USD** |
| ⚠️ Quên xóa NAT 1 tháng | | | **khoảng 33 USD** |

---

## 10. Dọn dẹp

Thứ tự xóa (ngược với lúc tạo, vì phụ thuộc lẫn nhau):

1. EC2 instance → EC2 Instance Connect Endpoint.
2. Route `0.0.0.0/0 → nat` trong `rt-app-a/b` → **NAT Gateway** (đợi trạng thái `deleted`) → **Release** Elastic IP.
3. (Nếu bỏ hẳn lab) Gateway Endpoint S3 → bỏ gắn NACL/route table → subnet → SG (xóa rule tham chiếu chéo trước) → detach + xóa IGW → VPC.

Trên Console, **Delete VPC** tự xóa luôn subnet, route table, IGW, SG, NACL đi kèm, nhưng **không** xóa được khi còn EC2, NAT hay ENI đang dùng. Sau khi dọn, kiểm tra:

```powershell
aws ec2 describe-nat-gateways --filter "Name=state,Values=available,pending" --query "NatGateways[].NatGatewayId" --region us-east-1
aws ec2 describe-addresses --query "Addresses[].[PublicIp,AssociationId]" --region us-east-1
aws ec2 describe-instances --filters "Name=instance-state-name,Values=running,stopped" --query "Reservations[].Instances[].InstanceId" --region us-east-1
```

Ba lệnh đều trả về `[]` thì không còn gì tính tiền theo giờ.
