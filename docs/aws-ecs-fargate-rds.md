# Phân tích: deploy RoomSplit bằng ECR, ECS Fargate và RDS

Tài liệu này phân tích việc thay mô hình EC2 + Docker Compose hiện tại bằng:

- **Amazon ECR** lưu Docker image của `RoomSplit.Api`;
- **Amazon ECS Fargate** chạy API, không phải tự quản lý EC2;
- **Amazon RDS for MySQL** thay cho container `mysql`;
- Redis giữ trong ECS hoặc chuyển sang ElastiCache ở bước riêng.

RDS là dịch vụ cơ sở dữ liệu, không thay thế ECS. ECS chạy API; RDS cung cấp MySQL
cho API kết nối qua endpoint private trong VPC.

## 1. Phạm vi và yêu cầu

### Trong phạm vi

1. Build image từ [Dockerfile](../Dockerfile), tag theo commit và push lên ECR.
2. Tạo ECS cluster, task definition và service Fargate cho API.
3. Trỏ `ConnectionStrings__DefaultConnection` tới RDS.
4. Chạy migration RDS bằng một quy trình riêng, không cho mọi task API tự migrate.
5. Dùng health check `/health/live` và `/health/ready`.
6. Giữ secret ngoài image và ngoài Git.

### Không đổi trong bước đầu

- Entity, EF Core, Pomelo và schema không đổi.
- API vẫn nghe cổng 8080 trong container.
- RDS dùng MySQL tương thích với cấu hình hiện tại; nên chọn MySQL 8.4 khi
  dựng mới và kiểm tra lại tương thích trước khi đổi local image.
- Redis chưa bắt buộc chuyển sang ElastiCache.

## 2. Bề mặt cần cập nhật

| Khu vực | Thay đổi dự kiến |
|---|---|
| ECR | Tạo repository, policy pull và quy ước tag image |
| ECS | Cluster, task definition, service, IAM execution/task role |
| VPC | Subnet app cho task, subnet public cho ALB, subnet data cho RDS |
| Security Group | ALB → ECS: 8080; ECS → RDS: 3306; ECS → Redis: 6379 |
| RDS | DB subnet group, instance, database `roomsplit`, app/migrator users |
| Secrets Manager | Lưu connection string hoặc từng thành phần DB/Redis |
| CI/CD | Build, push ECR, render task definition, deploy ECS |
| Tài liệu | Runbook migration, rollback, kiểm tra health và dọn tài nguyên |

## 3. Quy tắc kết nối

Luồng request và dữ liệu được tách như sau:

```text
Internet
   │ 80/443
   ▼
ALB (public subnets)
   │ 8080
   ▼
ECS Fargate task (private app subnets)
   ├── 3306 ──► RDS MySQL (private data subnets)
   └── 6379 ──► Redis (ECS service hoặc ElastiCache)
```

- RDS đặt `Public access = No`.
- Security Group của RDS chỉ nhận 3306 từ Security Group của ECS task.
- Không dùng IP của RDS trong cấu hình; dùng DNS endpoint.
- Không đặt AWS access key, mật khẩu DB hoặc connection string đầy đủ trong
  Docker image.
- ECS task role dùng cho quyền ứng dụng; ECS execution role dùng để pull ECR,
  ghi log CloudWatch và đọc secret theo đúng policy.

## 4. Các phương án

### Phương án A — Fargate private + ALB + RDS (khuyến nghị)

```text
ALB public-a/b → ECS Fargate private app-a/b → RDS private data-a/b
```

**Ưu điểm**

- Không public IP cho task và RDS.
- Có rolling deployment, health check và scale service.
- Phù hợp với kiến trúc VPC hiện có và production hơn.

**Nhược điểm**

- Cần ALB, NAT Gateway hoặc VPC endpoints, CloudWatch Logs và nhiều IAM hơn.
- Có chi phí nền ngay cả khi tải thấp.
- Cần cấu hình route, subnet và Security Group chính xác.

### Phương án B — Fargate public IP + RDS private

```text
Internet → ECS task public subnet/public IP → RDS private data subnet
```

**Ưu điểm**

- Ít thành phần hơn, dễ thử nghiệm.
- Có thể tránh NAT Gateway trong lab ngắn hạn.

**Nhược điểm**

- Task bị phơi trực tiếp ra Internet.
- IP task thay đổi khi deploy; không phù hợp production.
- Khó áp dụng HTTPS, health check và kiểm soát lưu lượng chuẩn.

### Phương án C — ECS Fargate private + ElastiCache Redis ngay từ đầu

Đây là Phương án A nhưng chuyển Redis sang ElastiCache thay vì chạy Redis
container.

**Ưu điểm**

- Redis được quản lý, không mất cache khi task API thay đổi.
- Không phải quản lý task Redis và mật khẩu Redis trong compose.

**Nhược điểm**

- Tăng chi phí và phạm vi thay đổi.
- Không cần thiết để kiểm chứng ECR/ECS/RDS trước.

## 5. Khuyến nghị

Chọn **Phương án A**, triển khai theo thứ tự:

1. RDS và Security Group kết nối từ app.
2. ECR và task definition tối thiểu.
3. ECS service trong private subnets.
4. ALB và health check.
5. Secrets Manager, migration task và rollback.
6. Chuyển Redis sang ElastiCache nếu cần HA hoặc nhiều task API.

Lý do: phương án này thay đúng EC2 bằng Fargate, thay đúng MySQL container bằng
RDS, đồng thời giữ các nguyên tắc mạng đã có trong
[aws-networking.md](./aws-networking.md). Phương án B chỉ nên dùng để lab ngắn
hạn; phương án C nên tách thành một thay đổi độc lập để dễ chẩn đoán lỗi.

## 6. Các quyết định cần chốt trước khi triển khai

1. Region triển khai: giữ `us-east-1` hay chuyển sang region khác?
2. Có dùng ALB HTTPS ngay không, hay bắt đầu bằng HTTP trong lab?
3. Redis giai đoạn đầu chạy bằng ECS task hay dùng ElastiCache?
4. Migration chạy bằng ECS one-off task hay từ máy quản trị qua tunnel?
5. CI/CD dùng GitHub Actions OIDC hay push ECR thủ công bằng AWS CLI?
6. RDS Single-AZ cho lab hay Multi-AZ cho production?

## 7. Điều kiện nghiệm thu dự kiến

- Image được pull thành công từ ECR bởi ECS execution role.
- Task đạt `RUNNING`, `/health/live` trả HTTP 200.
- ALB chỉ route tới task healthy.
- `/health/ready` trả HTTP 200 khi RDS và Redis sẵn sàng.
- API tạo/đọc dữ liệu được trên RDS.
- Restart hoặc rolling deploy task không làm mất dữ liệu.
- Không có secret trong image, task definition plaintext hoặc Git.
- Có thể rollback về image tag trước đó.

Sau khi chọn các quyết định ở mục 6, tài liệu triển khai chi tiết và các file cấu
hình liên quan mới được tạo/cập nhật.

## 8. Runbook kết nối đã chọn

Các bước dưới đây dùng `us-east-1`, ALB HTTP cho lab, Redis chạy bằng một ECS
service riêng, RDS Single-AZ, migration bằng ECS one-off task và thao tác AWS CLI
thủ công. Thay các giá trị `<...>` bằng giá trị của tài khoản; không ghi mật khẩu
thật vào shell history hoặc repository.

### 8.1 Tạo ECR và push image

> Đã có CI tự build + push khi merge `main` (tag = commit SHA, repo immutable): xem [deploy-ecr.md](deploy-ecr.md). Các lệnh dưới đây là cách push tay.

```powershell
$Region = "us-east-1"
$AccountId = (aws sts get-caller-identity --query Account --output text)
$Repository = "roomsplit-api"
$Registry = "$AccountId.dkr.ecr.$Region.amazonaws.com"
$Tag = (git rev-parse --short HEAD)

aws ecr create-repository --repository-name $Repository --region $Region
aws ecr get-login-password --region $Region |
  docker login --username AWS --password-stdin $Registry
docker build --platform linux/amd64 -t "$Registry/$Repository`:$Tag" .
docker push "$Registry/$Repository`:$Tag"
```

Chỉ tạo repository một lần. Với mỗi bản build mới, dùng tag commit bất biến; có
thể thêm tag `latest` cho lab nhưng không dùng tag này để rollback production.

### 8.2 Cấu hình RDS

1. Tạo DB subnet group gồm ít nhất hai data subnet trong hai AZ.
2. Tạo Security Group `roomsplit-rds`: inbound TCP 3306 chỉ từ Security Group
   của ECS task.
3. Tạo RDS MySQL Single-AZ, `Public access = No`, bật encryption và backup.
4. Tạo database `roomsplit`, migrator user và app user theo hướng dẫn ở mục 6
   của [aws-rds.md](./aws-rds.md).
5. Lưu connection string của app trong Secrets Manager. Giá trị phải có dạng:

```text
Server=<rds-endpoint>;Port=3306;Database=roomsplit;User=roomsplit_app;Password=<secret>;SslMode=Required;Maximum Pool Size=20;Connection Timeout=5;Default Command Timeout=30;
```

Không dùng master user cho API. RDS endpoint là DNS, không phải địa chỉ IP cố
định.

### 8.3 Tạo ECS task definition

Task definition cần có:

- execution role cho `ecr:GetAuthorizationToken`, pull image ECR, ghi
  CloudWatch Logs **và `secretsmanager:GetSecretValue` trên secret runtime**
  (xem 8.3.1 — ECS agent đọc secret bằng execution role, không phải task role);
- task role: chỉ cần khi **code** gọi AWS API (ví dụ SDK đọc S3); không cần cho
  `secrets[].valueFrom`;
- `CPU`/memory phù hợp lab, ví dụ 0.5 vCPU/1 GB;
- container port `8080`;
- `ASPNETCORE_ENVIRONMENT=Staging`;
- `ConnectionStrings__DefaultConnection` lấy từ Secrets Manager;
- `ConnectionStrings__Redis` lấy từ secret Redis;
- `Database__MigrateOnStartup=false`;
- log driver `awslogs`.

### 8.3.1 Biến môi trường đọc từ Secrets Manager (`secrets[].valueFrom`)

**Cơ chế:** khi task khởi động, ECS agent dùng **task execution role** gọi
`GetSecretValue`, rồi đặt giá trị thành biến môi trường của container. Code
không đổi gì: ASP.NET Core đọc env như bình thường (`__` = dấu `:` trong
config, nên `ConnectionStrings__DefaultConnection` →
`ConnectionStrings:DefaultConnection`).

#### a) Hai chỗ khai báo biến trong container definition

| Khối | Dùng cho | Hiện trong Console/API |
|---|---|---|
| `environment` (`name` + `value`) | Giá trị không nhạy cảm: `ASPNETCORE_ENVIRONMENT`, `Database__MigrateOnStartup` | Hiện plaintext |
| `secrets` (`name` + `valueFrom`) | Password, connection string | Chỉ hiện ARN, không hiện giá trị |

Không bao giờ dán connection string có password vào `environment`.

#### b) Cú pháp `valueFrom`

```text
arn:aws:secretsmanager:<region>:<account-id>:secret:<secret-name>-<6 ký tự>:<json-key>:<version-stage>:<version-id>
```

| Muốn lấy | `valueFrom` |
|---|---|
| Cả giá trị secret (secret dạng plaintext) | `arn:...:secret:roomsplit/lab/db-AbC123` |
| Một key trong secret JSON | `arn:...:secret:roomsplit/lab/runtime-AbC123:connectionString::` |
| Key JSON + phiên bản cụ thể | `arn:...:secret:roomsplit/lab/runtime-AbC123:connectionString:AWSPREVIOUS:` |

- Hai dấu `::` cuối là bắt buộc khi chỉ định key (để trống stage và version →
  lấy `AWSCURRENT`).
- Dùng ARN đầy đủ (có hậu tố 6 ký tự ngẫu nhiên, copy ở trang chi tiết secret).
- Đọc theo key JSON cần Fargate platform version `1.4.0` trở lên (mặc định
  `LATEST` đã thỏa).

Ví dụ secret `roomsplit/lab/runtime` kiểu JSON:

```json
{
  "connectionString": "Server=<rds-endpoint>;Port=3306;Database=roomsplit;User=roomsplit_app;Password=...;SslMode=Required;",
  "redis": "<redis-host>:6379,password=...,ssl=False,abortConnect=False"
}
```

#### c) Container definition mẫu (JSON)

```json
{
  "name": "api",
  "image": "<account-id>.dkr.ecr.us-east-1.amazonaws.com/roomsplit-api:<git-sha>",
  "portMappings": [{ "containerPort": 8080, "protocol": "tcp" }],
  "environment": [
    { "name": "ASPNETCORE_ENVIRONMENT", "value": "Staging" },
    { "name": "Database__MigrateOnStartup", "value": "false" }
  ],
  "secrets": [
    {
      "name": "ConnectionStrings__DefaultConnection",
      "valueFrom": "arn:aws:secretsmanager:us-east-1:<account-id>:secret:roomsplit/lab/runtime-AbC123:connectionString::"
    },
    {
      "name": "ConnectionStrings__Redis",
      "valueFrom": "arn:aws:secretsmanager:us-east-1:<account-id>:secret:roomsplit/lab/runtime-AbC123:redis::"
    }
  ]
}
```

#### d) Làm trên Console

1. ECS → **Task definitions** → chọn task definition → **Create new revision**.
2. Ở container `api` → mục **Environment variables** → **Add environment
   variable**.
3. Điền **Key** = `ConnectionStrings__DefaultConnection`, **Value type** =
   `ValueFrom`, **Value** = ARN ở bảng b).
4. Biến thường (không nhạy cảm) chọn Value type = `Value`.
5. **Create** → vào service → **Update service** chọn revision mới (hoặc
   **Force new deployment**).

#### e) Quyền cho execution role

Gắn inline policy vào execution role (ví dụ `roomsplit-ecs-execution`), bên
cạnh managed policy `AmazonECSTaskExecutionRolePolicy`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "secretsmanager:GetSecretValue",
      "Resource": "arn:aws:secretsmanager:us-east-1:<account-id>:secret:roomsplit/lab/runtime-*"
    }
  ]
}
```

- `-*` ở cuối để khớp hậu tố 6 ký tự ngẫu nhiên.
- Nếu secret mã hóa bằng **customer managed KMS key** thì thêm `kms:Decrypt`
  trên key đó. Dùng key mặc định `aws/secretsmanager` thì không cần.

#### f) Mạng

Task trong private subnet phải gọi được endpoint Secrets Manager: qua NAT
Gateway, hoặc qua **interface VPC endpoint**
`com.amazonaws.us-east-1.secretsmanager` (bật Private DNS, SG của endpoint cho
inbound 443 từ ECS SG). Thiếu đường đi này, task dừng trước khi container chạy.

#### g) Lưu ý và lỗi thường gặp

| Hiện tượng / tình huống | Nguyên nhân | Cách xử lý |
|---|---|---|
| Task STOPPED, reason `ResourceInitializationError: unable to pull secrets or registry auth` | Execution role thiếu `GetSecretValue`/`kms:Decrypt`, hoặc không có đường mạng tới Secrets Manager | Kiểm tra policy (mục e) và NAT/VPC endpoint (mục f) |
| Lỗi kiểu `retrieved secret ... did not contain json key` | Sai tên key JSON (phân biệt hoa thường) hoặc secret không phải JSON | So key trong secret với phần sau ARN |
| Đổi giá trị secret / rotation nhưng app vẫn dùng giá trị cũ | ECS chỉ đọc secret **lúc task khởi động** | `aws ecs update-service --force-new-deployment` |
| Muốn xem env trong container | Giá trị không hiện ở Console | ECS Exec (`aws ecs execute-command`) rồi `printenv` — chỉ để debug lab |
| Dùng SSM Parameter Store thay Secrets Manager | Cũng được: `valueFrom` = ARN của parameter, quyền `ssm:GetParameters` | Không đọc theo key JSON được |

Có thể tách thành hai secret riêng (DB, Redis) để policy và task definition dễ
kiểm tra hơn; một secret JSON nhiều key thì ít secret phải quản lý hơn (và rẻ
hơn: tính phí theo số secret).

### 8.4 Tạo ECS service và ALB

1. Tạo cluster ECS.
2. Tạo ALB trong hai public subnet.
3. Tạo target group kiểu `ip`, port `8080`, health check path
   `/health/ready`.
4. Cho ECS service chạy trong hai private app subnet, không gán public IP.
5. Gắn Security Group:
   - ALB: inbound TCP 80 từ nguồn lab; outbound 8080 tới ECS SG.
   - ECS: inbound 8080 chỉ từ ALB SG; outbound tới RDS SG 3306 và Redis SG
     6379.
   - RDS: inbound 3306 chỉ từ ECS SG.
6. Deploy service với image tag từ ECR. Chờ task `HEALTHY` trước khi mở lưu
   lượng.

### 8.5 Chạy migration bằng ECS one-off task

Không bật `Database__MigrateOnStartup` cho service có nhiều task. Tạo một task
definition migration riêng, dùng secret DB và chạy một image có `dotnet-ef` hoặc
`mysql` client. Image runtime hiện tại chỉ chứa output publish, nên không thể
chạy `dotnet ef` trực tiếp trong image đó. Quy trình được khuyến nghị là tạo
script SQL idempotent từ máy build, sau đó chạy một migration image/one-off task
để nạp script vào RDS:

```text
mysql -h <rds-endpoint> -u <migrator-user> -p roomsplit < migrate.sql
```

Tạo script SQL idempotent trước bằng:

```powershell
dotnet ef migrations script --idempotent `
  --project RoomSplit.Api `
  --output artifacts\migrate.sql
```

Sau đó chạy một image/tool migration có `mysql` client trong cùng VPC. Không
chạy migration đồng thời với nhiều task API.

### 8.6 Kiểm tra kết nối

```powershell
curl.exe "http://<alb-dns-name>/health/live"
curl.exe "http://<alb-dns-name>/health/ready"
curl.exe "http://<alb-dns-name>/api/v1/rooms"
```

Nếu `/health/live` thành công nhưng `/health/ready` timeout, kiểm tra theo thứ
 tự: route của private subnet, ECS SG → RDS SG, DNS trong VPC, secret và trạng
 thái RDS. Nếu API healthy nhưng request lỗi Redis, kiểm tra Redis service,
 Redis SG và chuỗi `ConnectionStrings__Redis`.

### 8.7 Rollback

Deploy lại task definition với image tag trước đó, không dùng `latest`. Không
rollback migration bằng cách xóa dữ liệu; migration phá hủy schema cần quy trình
backup/restore riêng. Trước mỗi migration, tạo snapshot hoặc backup phù hợp với
môi trường.
