# Amazon RDS for MySQL: từ cơ bản đến production, áp dụng cho RoomSplit

Tiếp theo [aws-networking.md](aws-networking.md), [aws-lab-architecture.md](aws-lab-architecture.md) và [aws-ec2-deploy.md](aws-ec2-deploy.md). Ở giai đoạn 2, MySQL chạy **trong container cạnh API trên cùng một EC2**. Bài này giải thích RDS là gì, cách dùng nó thay cho container `mysql`, các phương án áp dụng vào lab, và các sự cố hay gặp trên production cùng cách xử lý.

> Giá là mức tham khảo `us-east-1` tại thời điểm viết (11/2026), luôn kiểm tra lại trên trang giá của AWS. Mục 7 là **phương án**, chưa làm gì trên AWS; chờ chọn phương án rồi mới viết walkthrough.

## Mục lục
1. [RDS là gì, khác gì MySQL trong Docker](#1-rds-là-gì-khác-gì-mysql-trong-docker)
2. [Các khái niệm cơ bản](#2-các-khái-niệm-cơ-bản)
3. [Tính sẵn sàng: Multi-AZ và read replica](#3-tính-sẵn-sàng-multi-az-và-read-replica)
4. [Backup, snapshot, khôi phục](#4-backup-snapshot-khôi-phục)
5. [Chi phí và bẫy chi phí](#5-chi-phí-và-bẫy-chi-phí)
6. [Kết nối RoomSplit tới RDS](#6-kết-nối-roomsplit-tới-rds)
7. [Phương án áp dụng vào lab RoomSplit](#7-phương-án-áp-dụng-vào-lab-roomsplit)
8. [Nâng cao](#8-nâng-cao)
9. [Edge case hay gặp trên production và cách xử lý](#9-edge-case-hay-gặp-trên-production-và-cách-xử-lý)
10. [Thí nghiệm để "thấy tận mắt" các edge case](#10-thí-nghiệm-để-thấy-tận-mắt-các-edge-case)
11. [Checklist production](#11-checklist-production)

---

## 1. RDS là gì, khác gì MySQL trong Docker

**Amazon RDS** (Relational Database Service) là MySQL (hoặc PostgreSQL, MariaDB, SQL Server, Oracle) do AWS **vận hành hộ**. Bạn vẫn dùng đúng MySQL, đúng giao thức, đúng connection string; Pomelo/EF Core **không biết** mình đang nói chuyện với RDS. Cái khác là **ai chịu trách nhiệm** phần nào.

| Việc | MySQL trong Docker trên EC2 (giai đoạn 2) | RDS |
|---|---|---|
| Cài, nâng cấp MySQL | Bạn đổi tag image | AWS (bạn chọn version, cửa sổ bảo trì) |
| Vá hệ điều hành | Bạn (`dnf update`) | AWS |
| Backup hằng ngày, khôi phục tới từng giây | Bạn tự viết `mysqldump` + cron + đẩy S3 | Bật sẵn, chỉ chọn số ngày giữ |
| Ổ đĩa đầy | Container chết, bạn tự nới EBS | Có thể bật **storage autoscaling** |
| Máy chủ hỏng | Mất DB tới khi bạn dựng lại | Single-AZ: AWS tự thay máy; Multi-AZ: tự chuyển sang máy dự phòng |
| Giám sát | Tự cài | CloudWatch metric có sẵn |
| Quyền root của hệ điều hành, SSH vào máy DB | Có | **Không** (không SSH được vào máy RDS) |
| Quyền `SUPER` trong MySQL | Có | **Không** (master user có gần đủ quyền, trừ `SUPER`) |
| Chi phí | Chỉ tiền EC2 | Đắt hơn EC2 cùng cỡ khoảng 1,5–2 lần |

Đây là mô hình **shared responsibility**: AWS lo phần cứng, OS, cài đặt MySQL, backup; bạn vẫn lo **schema, index, câu query, user/quyền trong MySQL, mạng (SG), mật khẩu**.

**Khi nào không cần RDS:** dự án học tập, một máy, chấp nhận mất dữ liệu (như giai đoạn 2). **Khi nào nên dùng:** dữ liệu thật của người dùng (tiền nợ của các phòng trọ chính là dữ liệu không được phép mất).

---

## 2. Các khái niệm cơ bản

Mỗi khái niệm kèm một dòng "với RoomSplit" để thấy nó nằm ở đâu trong dự án.

### 2.1 DB instance
Một "máy MySQL" do RDS quản lý. Có **identifier** (tên, ví dụ `roomsplit-lab-db`), là duy nhất trong Region của tài khoản. Bên trong có thể chứa nhiều database (schema) như MySQL bình thường.
- Với RoomSplit: một instance, bên trong database `roomsplit`.

### 2.2 Engine và version
- **Major version**: 8.0, 8.4 (LTS). **Minor**: 8.4.x. Minor có thể bật **auto minor version upgrade** (chạy trong maintenance window); major thì **luôn phải tự nâng**.
- MySQL 8.0 đã hết vòng đời cộng đồng (4/2026). Trên RDS, 8.0 hết **standard support** giữa năm 2026; sau mốc đó vẫn chạy được nhưng bị tính phí **RDS Extended Support** theo vCPU-giờ (với máy nhỏ, phí này có thể **lớn hơn cả tiền máy**). Kiểm tra mốc chính xác ở trang *Amazon RDS for MySQL release calendar*.
- Với RoomSplit: tạo mới thì chọn **MySQL 8.4**. Local đang dùng image `mysql:8.0.46` và code khai báo `MySqlServerVersion(8, 0, 36)` (đây là "mức tính năng tối thiểu" để Pomelo sinh SQL, không phải version bắt buộc của server). Nên đổi image dev sang 8.4 để dev và cloud **cùng version** (edge case 9.14).

### 2.3 Instance class
Cỡ máy, dạng `db.<họ><thế hệ>.<cỡ>`:

| Họ | Ví dụ | Dùng khi |
|---|---|---|
| `t` (burstable) | `db.t4g.micro` (2 vCPU, 1 GiB) | Lab, tải nhẹ, lúc rảnh lúc bận. CPU chạy bằng **CPU credit** |
| `m` (general purpose) | `db.m7g.large` | Production tải đều |
| `r` (memory optimized) | `db.r7g.large` | DB lớn, cần nhiều RAM cho buffer pool |

`g` = chip Graviton (ARM), rẻ hơn Intel khoảng 10–20%; với RDS bạn không phải build gì cho ARM nên luôn ưu tiên `g`.
- Với RoomSplit lab: `db.t4g.micro`.

### 2.4 Storage
- Loại: **gp3** (mặc định, nên dùng), io1/io2 (IOPS cao, đắt), magnetic (cũ, đừng dùng).
- gp3 trên RDS MySQL: dưới 400 GB có sẵn 3.000 IOPS, 125 MB/s.
- **Chỉ tăng được, không giảm được.** Muốn nhỏ lại phải dump/restore sang instance mới.
- Sau mỗi lần sửa storage phải chờ khoảng **6 giờ** (hoặc tới khi "storage-optimization" xong) mới sửa tiếp được.
- **Storage autoscaling**: đặt "maximum storage threshold", RDS tự nới khi gần đầy.
- Với RoomSplit lab: 20 GB gp3, bật autoscaling tối đa 30 GB.

### 2.5 Endpoint
Tên DNS để kết nối, dạng `roomsplit-lab-db.xxxxxxxx.us-east-1.rds.amazonaws.com:3306`.
- Luôn dùng **endpoint**, không dùng IP: IP đổi khi failover, khi đổi cỡ máy, khi bảo trì.
- Với instance không public, endpoint vẫn phân giải được từ Internet nhưng ra **IP private** (`10.20.20.x`), nên chỉ kết nối được từ trong VPC (hoặc qua tunnel).
- Với RoomSplit: thay `Server=mysql` trong connection string bằng endpoint.

### 2.6 DB subnet group
Danh sách subnet mà RDS được phép đặt máy vào. **Bắt buộc ít nhất 2 AZ**, kể cả khi chạy Single-AZ (để sau này bật Multi-AZ hoặc AWS thay máy sang AZ khác).
- Với RoomSplit: `roomsplit-lab-db-subnets` = `data-a` + `data-b`. Đây chính là lý do lab có 2 subnet data ([aws-lab-architecture.md](aws-lab-architecture.md) mục D2).

### 2.7 Security Group và Public access
- RDS có ENI trong subnet nên được gắn **SG** như EC2. Lab đã có sẵn `roomsplit-lab-db`: inbound 3306 **chỉ từ SG `roomsplit-lab-app`**.
- **Public access = No**. Đặt Yes chỉ để thử nhanh, và khi đó phải nằm trong subnet có route ra IGW; đừng bao giờ mở 3306 cho `0.0.0.0/0`.
- Tầng data còn có NACL `nacl-data`: chỉ nhận 3306 từ `10.20.10.0/23` (app-a + app-b), outbound cổng ephemeral về đúng dải đó.

### 2.8 Master user
User quản trị tạo lúc dựng instance (ví dụ `admin`). Có hai cách giữ mật khẩu:
- **Self managed**: bạn tự đặt, tự cất (như `MYSQL_ROOT_PASSWORD` trong env file).
- **Managed in AWS Secrets Manager**: RDS tự sinh, tự xoay vòng (mặc định 7 ngày), khoảng 0,40 USD/secret/tháng.

**Không cho app dùng master user** (mục 6.2).

### 2.9 Parameter group và option group
- **Parameter group** = file `my.cnf` dạng quản lý. Group `default.mysql8.4` **không sửa được**: muốn đổi tham số phải tạo group riêng (`roomsplit-mysql84`) rồi gắn vào instance.
- Tham số **dynamic** áp dụng ngay; tham số **static** chỉ áp dụng sau **reboot** (instance hiện trạng thái `pending-reboot`).
- Tham số đáng quan tâm với RoomSplit:

| Tham số | Mặc định | Gợi ý | Vì sao |
|---|---|---|---|
| `time_zone` | UTC | **Giữ UTC** | `created_at` mặc định `CURRENT_TIMESTAMP(6)` sinh ở DB. App tính "hôm nay" qua `IAppClock` (Asia/Ho_Chi_Minh), không dựa vào giờ DB |
| `require_secure_transport` | `1` trên 8.4 (kiểm tra lại) | `1` | Bắt buộc TLS |
| `slow_query_log`, `long_query_time` | tắt, 10 | bật, `1` | Bắt query chậm |
| `max_connections` | Tính theo RAM, `db.t4g.micro` chỉ khoảng 60–90 | Để mặc định, chỉnh pool phía app | Edge case 9.2 |
| `lower_case_table_names` | `0` | **Giữ 0** | Chỉ đặt được **lúc tạo**. Liên quan bảng `__EFMigrationsHistory` (CLAUDE.md có ghi chú khi copy DB từ Windows) |

- **Option group**: tính năng cộng thêm (ví dụ audit plugin). RoomSplit chưa cần.

### 2.10 Maintenance window và backup window
- **Maintenance window**: 30 phút mỗi tuần để AWS vá OS hoặc nâng minor; có thể **khởi động lại DB** (Single-AZ mất kết nối vài phút).
- **Backup window**: lúc chụp snapshot hằng ngày (Single-AZ có thể chậm I/O một chút).
- Đặt cả hai vào giờ ít người dùng nhất theo **giờ Việt Nam** (Console hiển thị UTC: 03:00 VN = 20:00 UTC hôm trước).

### 2.11 Trạng thái instance hay gặp
`creating` → `backing-up` → `available`; `modifying`, `rebooting`, `storage-optimization`, `stopped`, `storage-full`, `incompatible-parameters` (parameter sai làm MySQL không khởi động được).

---

## 3. Tính sẵn sàng: Multi-AZ và read replica

Ba thứ hay bị nhầm với nhau:

| | Single-AZ | **Multi-AZ instance** | **Multi-AZ cluster** | **Read replica** |
|---|---|---|---|---|
| Mục đích | Rẻ | Chịu lỗi (HA) | HA + đọc | Chia tải **đọc** |
| Máy phụ | Không | 1 standby ở AZ khác, **không đọc được** | 2 standby, **đọc được** | 1..15 replica, đọc được |
| Sao chép | | **Đồng bộ** (ghi xong ở cả 2 mới trả lời) | Bán đồng bộ | **Bất đồng bộ** (có độ trễ) |
| Khi primary hỏng | AWS dựng lại, vài phút đến lâu hơn | Tự failover, khoảng 60–120 giây | Tự failover, khoảng 35 giây | **Không** tự failover; phải "promote" bằng tay |
| Endpoint | 1 | 1 (không đổi; DNS trỏ sang máy mới) | Writer + reader | Mỗi replica 1 endpoint riêng |
| Giá | x1 | x2 | x3 | x1 mỗi replica |

Ý chính:
- **Multi-AZ là để không mất DB, không phải để chạy nhanh hơn.**
- **Read replica là để đọc nhiều, không phải để backup** (xóa nhầm trên primary thì replica cũng xóa theo).
- Với RoomSplit: tải rất nhỏ, không cần replica. Production thật nên Multi-AZ instance. Lab dùng Single-AZ, chỉ bật Multi-AZ tạm 1 giờ để thử failover (mục 10).

---

## 4. Backup, snapshot, khôi phục

| | Automated backup | Manual snapshot |
|---|---|---|
| Tạo | Tự động hằng ngày + lưu binlog liên tục | Bạn bấm (hoặc AWS Backup) |
| Giữ | 1–35 ngày (**0 = tắt backup**) | Tới khi bạn xóa |
| Point-in-time restore (PITR) | Có, tới từng giây (trễ khoảng 5 phút so với hiện tại) | Không, chỉ đúng thời điểm chụp |
| Khi xóa instance | Mất theo (trừ khi chọn "Retain automated backups") | Còn |

Ba điều người mới hay hiểu sai:
1. **Restore luôn tạo instance MỚI, endpoint MỚI.** Không có nút "quay instance hiện tại về hôm qua". Muốn app dùng bản khôi phục: đổi connection string, hoặc đổi tên (rename) instance cũ rồi đặt tên instance mới trùng tên cũ để giữ endpoint.
2. Instance khôi phục dùng **parameter group và SG mặc định** nếu bạn không chọn lại. Quên chọn SG thì app không kết nối được.
3. Backup chỉ có giá trị khi **đã thử restore**. Diễn tập ít nhất một lần (mục 10).

Bổ sung: snapshot copy sang Region khác (chống mất cả Region), AWS Backup để gom lịch backup nhiều dịch vụ, và `mysqldump` thủ công trước các migration rủi ro.

---

## 5. Chi phí và bẫy chi phí

Tài khoản lab là **Free plan** (credit, không phải 12 tháng miễn phí kiểu cũ): mọi thứ dưới đây đều **trừ vào credit**.

| Khoản | Giá tham khảo | Lab chạy 4 giờ | Chạy cả tháng |
|---|---|---|---|
| `db.t4g.micro` Single-AZ | khoảng 0,016 USD/giờ | khoảng 0,07 USD | khoảng 12 USD |
| Multi-AZ | x2 | | khoảng 24 USD |
| gp3 20 GB | khoảng 0,115 USD/GB-tháng | ~0 | khoảng 2,3 USD |
| Backup | Miễn phí tới bằng dung lượng DB | 0 | 0 |
| Secrets Manager (nếu dùng) | 0,40 USD/secret/tháng | ~0 | 0,40 USD |
| Public IPv4 (nếu Public access = Yes) | 0,005 USD/giờ | | 3,6 USD |

**Bẫy hay gặp:**
- **Stop không phải là tắt mãi.** Instance bị stop sẽ **tự start lại sau 7 ngày**. Không dùng nữa thì **xóa** (có thể chụp final snapshot trước). Khi stop vẫn tính tiền storage và snapshot.
- **Extended Support** cho MySQL 8.0 (mục 2.2): chọn 8.4 để tránh.
- Snapshot thủ công **không tự xóa**, tính phí theo GB-tháng khi vượt mức miễn phí.
- **Enhanced Monitoring**, Performance Insights trả phí, log export lên CloudWatch Logs đều có thể phát sinh phí nhỏ; lab nên tắt.
- Xóa instance mà bật **deletion protection** sẽ bị chặn: phải Modify tắt trước (đây là tính năng, không phải lỗi).

---

## 6. Kết nối RoomSplit tới RDS

### 6.1 App gần như không phải sửa code
Code đã sẵn những thứ RDS cần:

| Có sẵn | Ở đâu | Vì sao cần với RDS |
|---|---|---|
| Connection string đọc từ config/env | `Program.cs`, `ConnectionStrings__DefaultConnection` | Chỉ cần đổi `Server=` thành endpoint |
| `EnableRetryOnFailure()` | `Program.cs` | Tự thử lại khi failover, reboot bảo trì, deadlock |
| `MigrateOnStartup` mặc định tắt ngoài Development | `Program.cs` | Không để nhiều instance API cùng chạy migration |
| `/health/ready` kiểm tra DB | `Program.cs` | Load balancer biết lúc DB mất kết nối |
| Migration bằng script idempotent | [docker-multi-env.md](docker-multi-env.md) | Chạy được với RDS qua `mysql` client |

Cái **thay đổi** nằm ở compose: `docker-compose.prod.yml` đang ghép `Server=mysql;...` và service `api` phụ thuộc service `mysql`. Với RDS cần bỏ container `mysql` và truyền endpoint vào (mục 7 sẽ quyết định cách làm).

### 6.2 User và quyền trong MySQL
Tách 3 user, mỗi user làm đúng một việc:

| User | Quyền | Ai dùng |
|---|---|---|
| `admin` (master) | Gần như tất cả | Chỉ người quản trị, tạo các user bên dưới |
| `roomsplit_migrator` | `ALL` trên `roomsplit.*` | Chạy `migrate.sql` lúc deploy |
| `roomsplit_app` | `SELECT, INSERT, UPDATE, DELETE` trên `roomsplit.*` | API lúc chạy |

```sql
CREATE DATABASE roomsplit CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

CREATE USER 'roomsplit_migrator'@'%' IDENTIFIED BY '<mật khẩu 1>';
GRANT ALL PRIVILEGES ON roomsplit.* TO 'roomsplit_migrator'@'%';

CREATE USER 'roomsplit_app'@'%' IDENTIFIED BY '<mật khẩu 2>';
GRANT SELECT, INSERT, UPDATE, DELETE ON roomsplit.* TO 'roomsplit_app'@'%';
```

`'%'` (mọi host) là chấp nhận được vì **mạng** đã được SG + NACL khóa; host trong MySQL không phải lớp bảo vệ chính. Lợi ích: lỡ có lỗ SQL injection thì user app cũng không `DROP TABLE` được. Khác biệt với compose hiện tại: image `mysql` cấp `ALL` cho `MYSQL_USER`, còn ở đây app user không đổi được schema.

### 6.3 Connection string gợi ý
```
Server=<endpoint>;Port=3306;Database=roomsplit;User=roomsplit_app;Password=<...>;
SslMode=Required;
Maximum Pool Size=20;Connection Timeout=5;Default Command Timeout=30;
```

| Tham số (MySqlConnector) | Ý nghĩa | Gợi ý |
|---|---|---|
| `SslMode=Required` | Bắt buộc mã hóa, **không** kiểm tra chứng chỉ | Đủ cho lab |
| `SslMode=VerifyFull` + `SslCa=<global-bundle.pem>` | Mã hóa **và** kiểm tra đúng là server RDS | Production; phải đưa file CA của RDS vào image |
| `Maximum Pool Size` | Số kết nối tối đa **mỗi process** (mặc định 100) | Tổng các process phải nhỏ hơn `max_connections` (edge case 9.2) |
| `Connection Timeout` | Chờ mở kết nối (giây) | 5: lỗi mạng thì báo nhanh, không treo request |
| `Default Command Timeout` | Chờ một câu lệnh | 30 |
| `Connection Lifetime` | Đóng kết nối sống quá N giây | 300 nếu dùng failover nhiều (edge case 9.1) |

Mật khẩu: lab giữ trong `envs/aws-lab.env` như hiện tại (git-ignored). Production: **Secrets Manager** hoặc **SSM Parameter Store (SecureString)**, EC2/ECS đọc qua IAM role, không có mật khẩu nằm trên đĩa.

### 6.4 Chạy migration lên RDS
RDS nằm trong subnet data, laptop không kết nối thẳng được. Hai đường:

**(a) Từ một máy trong VPC.** SSH vào EC2 (qua EICE), cài `mysql` client, đẩy script:
```bash
# Trên laptop
dotnet ef migrations script --idempotent -o artifacts/migrate.sql --project RoomSplit.Api
# Copy lên EC2 rồi trên EC2:
mysql -h <endpoint> -u roomsplit_migrator -p --ssl-mode=REQUIRED roomsplit < migrate.sql
```

**(b) Tunnel từ laptop qua EICE.** EICE mở được TCP tới **IP private** bất kỳ trong VPC, không chỉ cổng 22:
```bash
# Lấy IP private hiện tại của RDS (IP này có thể đổi, phải lấy lại mỗi lần)
nslookup <endpoint>
aws ec2-instance-connect open-tunnel \
  --instance-connect-endpoint-id <eice-id> \
  --private-ip-address <ip-rds> --remote-port 3306 --local-port 13306
# Cửa sổ khác: dotnet ef database update chạy qua 127.0.0.1:13306
```
Muốn đường (b) chạy trong lab cần sửa 3 chỗ (kiểm tra bằng CLI ngày 01/11/2026):
1. Policy `roomsplit-lab-eice-connect` đang có điều kiện `ec2-instance-connect:remotePort = 22` → root sửa thành `["22", "3306"]`.
2. SG `roomsplit-lab-eice` thêm outbound 3306 → SG `roomsplit-lab-db`.
3. SG `roomsplit-lab-db` thêm inbound 3306 từ SG `roomsplit-lab-eice`.

NACL `nacl-data` không cần sửa vì EICE nằm ở `app-a` (`10.20.10.0/24`), đã nằm trong dải được phép. Tunnel EICE tối đa 1 giờ, sau đó mở lại.

---

## 7. Phương án áp dụng vào lab RoomSplit

### 7.1 Hiện trạng (kiểm tra bằng CLI ngày 01/11/2026)
- VPC `roomsplit-lab-vpc` `10.20.0.0/16` còn nguyên: subnet, route table, `nacl-data`, EICE ở `app-a`.
- Không có EC2 nào đang chạy (máy `web` của giai đoạn 2 đã xóa). Chưa có RDS, chưa có DB subnet group.
- SG `roomsplit-lab-db`: inbound 3306 chỉ từ `roomsplit-lab-app`.
- Ở giai đoạn 2, API chạy trên máy `web` đặt ở **public-b** (`10.20.1.0/24`), SG `roomsplit-lab-web`. Vì vậy nếu chỉ thay MySQL bằng RDS mà giữ nguyên cách deploy cũ thì **cả SG `db` lẫn NACL `nacl-data` đều chặn** máy `web`.
- Profile `dev-readonly` có `AmazonRDSFullAccess` nên tạo RDS được. Lúc tạo nhớ **tắt Enhanced Monitoring**: nó cần tạo IAM role, mà profile này không có quyền đó.

### 7.2 Các quyết định

| # | Quyết định | Lựa chọn |
|---|---|---|
| Q1 | API chạy ở đâu, đi vào từ Internet thế nào | R1 / R2 / R3 bên dưới |
| Q2 | Single-AZ hay Multi-AZ | Single-AZ, bật Multi-AZ tạm khi thí nghiệm failover |
| Q3 | Version | 8.4 (tránh Extended Support của 8.0) |
| Q4 | Mật khẩu | Tự quản trong `envs/aws-lab.env` hay Secrets Manager |
| Q5 | Compose khi dùng RDS | File override mới hay `--no-deps` |

### 7.3 Q1: ba phương án

**R1. Giữ máy `web` ở public subnet, chỉ thay container `mysql` bằng RDS**
```
Internet → web (public-b, Docker: api + redis) → RDS (data-a)
```
- Sửa mạng: SG `db` thêm inbound 3306 từ SG `web`; NACL `nacl-data` thêm inbound 3306 và outbound ephemeral cho `10.20.1.0/24`.
- Ưu: dùng lại gần như toàn bộ walkthrough giai đoạn 2; rẻ nhất có truy cập Internet (EC2 + RDS, không ALB); tập trung đúng vào việc học RDS.
- Nhược: **phá nguyên tắc "tầng data chỉ tin tầng app"** của thiết kế; máy chạy API có IP public. Chỉ chấp nhận được như bước đệm.

**R2. API chuyển vào app subnet, ALB ở public subnet (hình dạng production)**
```
Internet → ALB (public-a/b) → app EC2 (app-a, SG app) → RDS (data-a)
```
- Mạng **không cần sửa**: SG `alb` → `app` → `db` và `nacl-data` đã thiết kế đúng cho luồng này. Image vẫn đưa vào bằng `docker save` + scp qua EICE nên không cần NAT.
- Ưu: đúng kiến trúc mục tiêu; học thêm ALB, target group, health check `/health/ready`.
- Nhược: hai thứ mới cùng lúc (RDS + ALB), khó biết lỗi nằm ở đâu; thêm phí ALB (khoảng 0,0225 USD/giờ + LCU).

**R3. App EC2 trong app subnet, không ALB, chỉ thử qua tunnel EICE**
- Ưu: rẻ, mạng sạch, không sửa SG/NACL.
- Nhược: không gọi được API từ Internet, kém trực quan hơn giai đoạn 2.

### 7.4 Q4 và Q5
- **Q4**: lab giữ **tự quản** (giống hiện tại, không tốn phí, không cần quyền IAM mới). Secrets Manager để dành cho phần nâng cao (đọc secret bằng IAM role của EC2, mà profile hiện chưa có quyền tạo role).
- **Q5**: thêm `docker-compose.rds.yml` (override khi dùng RDS: `api` lấy `ConnectionStrings__DefaultConnection` thẳng từ env file, ví dụ biến `DB_CONNECTION`), chạy `docker compose ... up -d --no-deps api redis` để không khởi động container `mysql`. Cách khác là tách hẳn `mysql` khỏi `docker-compose.yml` dùng chung, nhưng như vậy phải sửa dev/staging/prod đang chạy ổn, nên không đề xuất.

### 7.5 Đề xuất
**R2 + Single-AZ + MySQL 8.4 + mật khẩu tự quản + `docker-compose.rds.yml`**, nhưng làm **theo hai bước** để lỗi không chồng lên nhau:
1. Dựng RDS, tạo user (mục 6.2), mở tunnel EICE (mục 6.4 b) và chạy `dotnet ef database update` từ laptop. Đến đây RDS chạy độc lập, kiểm chứng được bằng `mysql` client.
2. Dựng app EC2 trong `app-a` + ALB, trỏ API vào RDS.

Lý do chọn R2 thay vì R1: mạng lab đã dựng sẵn đúng cho R2, nên R2 không phải sửa SG/NACL. R1 trông đơn giản hơn nhưng phải **mở thêm lỗ** ở tầng data rồi sau này lại đóng. Nếu muốn tiết kiệm thời gian và chi phí ALB thì R3 là bản rút gọn của R2 (chỉ khác là không có ALB).

---

## 8. Nâng cao

| Chủ đề | Là gì | Khi nào cần với RoomSplit |
|---|---|---|
| **Encryption at rest (KMS)** | Mã hóa ổ đĩa, snapshot, log. **Chỉ chọn được lúc tạo**; muốn mã hóa DB đang chạy phải snapshot → copy có mã hóa → restore | Luôn bật (mặc định đã bật, dùng key `aws/rds` miễn phí) |
| **Encryption in transit (TLS)** | `require_secure_transport` + `SslMode=VerifyFull` | Production |
| **IAM database authentication** | Đăng nhập MySQL bằng token IAM sống 15 phút thay mật khẩu | Khi muốn bỏ hẳn mật khẩu; giới hạn tốc độ mở kết nối mới, hợp với app có connection pool |
| **Secrets Manager rotation** | Tự đổi mật khẩu định kỳ | Production; app phải đọc lại secret khi đăng nhập lỗi (edge case 9.6) |
| **RDS Proxy** | Gom kết nối, failover nhanh hơn, giữ kết nối khi DB đổi máy | Khi có nhiều instance API hoặc Lambda; RoomSplit hiện chưa cần |
| **Performance Insights / CloudWatch Database Insights** | Xem câu query nào tốn tải nhất | Khi API chậm mà không rõ vì sao |
| **Enhanced Monitoring** | Metric OS mỗi 1–60 giây | Khi cần nhìn CPU/RAM chi tiết hơn CloudWatch |
| **Log export** | Đẩy error log, slow query log lên CloudWatch Logs | Production |
| **Blue/Green Deployments** | Dựng bản sao "green" (version mới, parameter mới), đồng bộ bằng replication, rồi switchover trong khoảng 1 phút | Nâng 8.0 → 8.4, đổi parameter static ít downtime |
| **Cross-Region snapshot copy / read replica** | Chống mất cả Region | Ngoài phạm vi MVP |
| **Aurora MySQL** | Engine của AWS tương thích MySQL, storage tách rời, failover nhanh | Đắt hơn; RoomSplit không cần |
| **IaC (CloudFormation / Terraform)** | Viết RDS bằng code | Bước sau khi đã làm tay một lần |

---

## 9. Edge case hay gặp trên production và cách xử lý

### 9.1 Failover hoặc reboot làm request lỗi vài chục giây
- **Triệu chứng**: lỗi `Unable to connect`, `Lost connection to MySQL server during query` (2013) hàng loạt trong 30–120 giây, sau đó tự hết.
- **Nguyên nhân**: Multi-AZ failover đổi DNS của endpoint sang máy khác; kết nối cũ trong pool trỏ vào máy chết. Maintenance window và đổi instance class cũng gây hiện tượng này.
- **Xử lý**:
  - RoomSplit đã có `EnableRetryOnFailure()`. Transaction tự mở bằng tay phải bọc trong `CreateExecutionStrategy().ExecuteAsync(...)` (đã ghi trong CLAUDE.md), nếu không retry sẽ ném lỗi.
  - Retry có thể **ghi trùng**: request tạo expense đã commit nhưng mất kết nối trước khi nhận kết quả, retry ghi thêm lần nữa. Với API tạo expense/settlement sắp làm, cân nhắc **idempotency key** (client gửi kèm một mã, server bỏ qua nếu đã xử lý).
  - Không cache IP của DB; dùng endpoint. Đặt `Connection Lifetime` để kết nối cũ không sống mãi.
  - Đặt maintenance window vào giờ thấp điểm, theo dõi sự kiện qua **RDS Event Subscription** (gửi SNS email).

### 9.2 `Too many connections` (lỗi 1040)
- **Nguyên nhân**: `max_connections` của máy nhỏ rất thấp (`db.t4g.micro` khoảng 60–90). Mỗi process API mở tới `Maximum Pool Size` (mặc định 100) kết nối. Hai container API là đã có thể vượt.
- **Xử lý**: tính `số instance API × Maximum Pool Size + kết nối cho admin/migration < max_connections`; đặt `Maximum Pool Size` rõ ràng. Theo dõi metric `DatabaseConnections`. Nhiều instance hơn nữa thì dùng RDS Proxy hoặc nâng instance class. Đừng tăng `max_connections` vượt quá mức RAM chịu được: mỗi kết nối tốn RAM.

### 9.3 Ổ đĩa đầy (`storage-full`)
- **Triệu chứng**: mọi lệnh ghi lỗi (`The table is full`, 1114), instance chuyển trạng thái `storage-full`.
- **Nguyên nhân**: dữ liệu tăng, binlog, bảng tạm của query lớn, slow log để lâu.
- **Xử lý**: bật **storage autoscaling** ngay từ đầu; alarm CloudWatch `FreeStorageSpace` dưới 20%. Nhớ: sau mỗi lần nới phải chờ khoảng 6 giờ mới nới tiếp được, nên đừng đợi sát 0 mới xử lý.

### 9.4 Hết CPU credit trên máy `t`
- **Triệu chứng**: DB đột nhiên chậm kéo dài dù không có thay đổi gì.
- **Nguyên nhân**: máy burstable dùng hết `CPUCreditBalance`; ở chế độ standard bị bóp về mức baseline, ở chế độ unlimited (mặc định trên RDS) thì không chậm nhưng **tính thêm tiền**.
- **Xử lý**: alarm `CPUCreditBalance` và `CPUUtilization`. Tải đều cả ngày thì chuyển sang `m7g`.

### 9.5 App kết nối không được: timeout hay bị từ chối
Phân biệt hai lỗi này là kỹ năng quan trọng nhất:

| Lỗi | Thường do | Kiểm tra |
|---|---|---|
| **Timeout** (chờ rồi hết giờ) | Mạng: SG thiếu rule, NACL thiếu chiều về, sai subnet/route, RDS không public | SG `db` có nhận từ SG của máy app không? NACL có cả inbound 3306 **và** outbound ephemeral không? |
| **Access denied** (1045) | Sai user/mật khẩu, user không có quyền từ host đó | Đăng nhập thử bằng `mysql` client; xem có đang dùng nhầm mật khẩu cũ (sau rotation) không |
| **Unknown database** | Chưa tạo database `roomsplit` | `CREATE DATABASE` (mục 6.2) |
| **SSL connection error** | Sai `SslMode`, thiếu CA, CA hết hạn | Mục 9.7 |
| **Unknown host** | Sai endpoint, hoặc VPC tắt DNS resolution | `nslookup <endpoint>` |

Với lab: máy API ở public subnet (R1) sẽ ra **timeout** vì cả SG `db` lẫn `nacl-data` đều không có rule cho nó.

### 9.6 Đổi mật khẩu (rotation) làm app sập
- **Nguyên nhân**: Secrets Manager xoay mật khẩu nhưng app vẫn giữ mật khẩu cũ đọc lúc khởi động; kết nối mới bị 1045.
- **Xử lý**: lúc đăng nhập lỗi thì đọc lại secret, hoặc dùng chiến lược **alternating users** (hai user thay phiên đổi mật khẩu), hoặc dùng IAM auth/RDS Proxy. Với self-managed: tạo mật khẩu mới, cập nhật env, restart app, rồi mới bỏ mật khẩu cũ.

### 9.7 Chứng chỉ CA của RDS hết hạn
- **Nguyên nhân**: app dùng `VerifyFull` với file CA cũ, AWS xoay CA (đợt `rds-ca-2019` hết hạn năm 2024 làm nhiều app sập).
- **Xử lý**: dùng **global bundle** của RDS (chứa nhiều CA), theo dõi mục *Pending maintenance* / thông báo CA trên Console, chủ động đổi CA của instance trong lịch bảo trì.

### 9.8 Migration khóa bảng, API treo
- **Triệu chứng**: deploy xong API timeout hàng loạt, `SHOW PROCESSLIST` thấy `Waiting for table metadata lock`.
- **Nguyên nhân**: `ALTER TABLE` cần metadata lock; nếu đang có transaction dài giữ bảng, ALTER phải chờ, và **mọi query sau** xếp hàng sau ALTER.
- **Xử lý**: kiểm tra script trước khi chạy; ưu tiên thay đổi MySQL 8 làm được `ALGORITHM=INSTANT` (thêm cột cuối bảng); đặt `lock_wait_timeout` ngắn cho phiên migration để nó **tự hủy** thay vì làm treo cả app; chạy giờ thấp điểm. Thay đổi phá vỡ (đổi tên/xóa cột) làm theo kiểu **expand/contract**: thêm cái mới, deploy code dùng cả hai, rồi mới xóa cái cũ. Nhớ thêm lỗi 1553 về index FK đã ghi trong CLAUDE.md mục 6.

### 9.9 Deadlock và lock wait timeout (1213, 1205)
- **Nguyên nhân**: hai request cùng ghi các dòng theo thứ tự ngược nhau. Với RoomSplit dễ gặp khi sau này tạo nhiều expense và settlement cùng lúc trong một phòng.
- **Xử lý**: transaction ngắn, ghi các bảng theo thứ tự cố định, có index cho cột trong `WHERE` của `UPDATE`. `EnableRetryOnFailure` coi deadlock là lỗi tạm thời và tự thử lại.

### 9.10 Đọc từ replica thấy dữ liệu cũ (replica lag)
- **Triệu chứng**: tạo expense xong, gọi API balance thấy chưa đổi.
- **Xử lý**: theo dõi `ReplicaLag`; dữ liệu vừa ghi (balance sau khi tạo expense/settlement) phải đọc từ **primary**. Chỉ đưa lên replica các báo cáo chấp nhận trễ vài giây.

### 9.11 Xóa nhầm dữ liệu hoặc xóa nhầm cả instance
- **Xử lý dữ liệu**: PITR ra instance mới tại thời điểm ngay trước lệnh xóa, lấy dữ liệu cần thiết rồi chép ngược về (mục 4). Không ghi đè cả DB nếu chỉ hỏng một phòng.
- **Phòng instance**: bật **deletion protection**; chụp **final snapshot** khi xóa; chọn "Retain automated backups"; giới hạn quyền `rds:DeleteDBInstance` bằng IAM.

### 9.12 Bảo trì bắt buộc và nâng version ngoài ý muốn
- **Nguyên nhân**: minor version hết hỗ trợ thì AWS **ép nâng** trong maintenance window; major hết standard support thì bị tính phí Extended Support.
- **Xử lý**: theo dõi *Pending maintenance actions*; thử version mới trên staging (cùng image `mysql:<version>` ở local); nâng major bằng **Blue/Green Deployments**.

### 9.13 Đổi parameter mà "không có tác dụng"
- **Nguyên nhân**: tham số static, instance đang `pending-reboot`; hoặc sửa nhầm parameter group không gắn với instance.
- **Xử lý**: xem cột "Parameter group" của instance; reboot trong giờ thấp điểm. Đặt sai tham số có thể làm instance `incompatible-parameters` và không khởi động được: sửa lại group rồi reboot.

### 9.14 Dev và production khác nhau
- **Ví dụ**: dev MySQL 8.0 trong Docker, RDS 8.4; dev dùng root, prod dùng user ít quyền nên migration lỗi `command denied`; dev giờ máy UTC+7, RDS UTC; bảng `__EFMigrationsHistory` khác hoa thường giữa Windows và Linux.
- **Xử lý**: cùng major version ở mọi môi trường; staging cũng dùng user tách quyền (mục 6.2); không phụ thuộc giờ của DB (RoomSplit đã dùng `IAppClock`); giữ `lower_case_table_names=0`.

### 9.15 Hóa đơn tăng mà không biết vì sao
- **Nguyên nhân**: instance bị stop **tự start sau 7 ngày**; snapshot thủ công tích lũy; Extended Support; Multi-AZ quên tắt sau thí nghiệm; public IPv4.
- **Xử lý**: tag mọi tài nguyên (`Project=roomsplit-lab`), xem Cost Explorer theo tag, xóa thay vì stop khi kết thúc lab. Lab không có quyền Budgets ở `dev-readonly`, nên root đặt AWS Budget.

---

## 10. Thí nghiệm để "thấy tận mắt" các edge case

Làm trong lab sau khi đã có RDS (mỗi thí nghiệm vài phút, tốn vài cent).

| # | Thí nghiệm | Làm gì | Quan sát |
|---|---|---|---|
| T1 | Timeout vs Access denied | Xóa tạm rule inbound của SG `db` rồi kết nối; trả rule lại rồi dùng sai mật khẩu | Lần 1 **treo** tới timeout; lần 2 báo **1045** ngay |
| T2 | Failover | Modify bật Multi-AZ, rồi **Reboot with failover**, trong lúc đó gọi API liên tục | `nslookup` endpoint đổi IP; API lỗi vài chục giây rồi tự lành nhờ retry. Xong thì tắt Multi-AZ |
| T3 | Too many connections | Đặt `Maximum Pool Size=200`, bắn nhiều request song song (ví dụ script trong `scripts/`) | Lỗi 1040; metric `DatabaseConnections` chạm trần |
| T4 | Parameter static | Tạo parameter group riêng, đổi một tham số static | Instance hiện `pending-reboot` tới khi reboot |
| T5 | Restore | Ghi chú thời điểm, xóa một room, PITR về trước thời điểm đó | Instance **mới**, endpoint **mới**; phải chọn lại SG |
| T6 | Metadata lock | Phiên 1 mở transaction đọc bảng `members` và không commit; phiên 2 `ALTER TABLE members ADD COLUMN x INT`; phiên 3 `SELECT` | Phiên 3 cũng treo vì xếp hàng sau ALTER |

---

## 11. Checklist production

- [ ] MySQL 8.4, `auto minor version upgrade` bật, maintenance/backup window giờ thấp điểm (giờ VN)
- [ ] Multi-AZ, instance class không phải `t` nếu tải đều
- [ ] Subnet private, Public access = No, SG chỉ nhận từ SG của app
- [ ] Encryption at rest bật; TLS bắt buộc, app dùng `VerifyFull` với global bundle
- [ ] Backup retention ≥ 7 ngày, đã diễn tập restore, deletion protection bật
- [ ] Storage autoscaling + alarm `FreeStorageSpace`, `CPUUtilization`, `DatabaseConnections`, `FreeableMemory`
- [ ] Ba user tách quyền (admin / migrator / app); mật khẩu trong Secrets Manager hoặc Parameter Store
- [ ] `Maximum Pool Size` × số instance < `max_connections`
- [ ] Parameter group riêng, slow query log bật và export lên CloudWatch Logs
- [ ] RDS Event Subscription gửi email cho failover, bảo trì, storage
- [ ] Migration chạy một lần mỗi deploy (script idempotent), không chạy lúc app khởi động, có `lock_wait_timeout`
- [ ] Tag tài nguyên, có Budget, theo dõi Extended Support
