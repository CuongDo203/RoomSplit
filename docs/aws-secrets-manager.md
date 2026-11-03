# AWS Secrets Manager: từ cơ bản đến nâng cao, áp dụng cho RoomSplit

Tiếp theo [iam-notes.md](iam-notes.md), [aws-ec2-deploy.md](aws-ec2-deploy.md) và [aws-rds.md](aws-rds.md) (mục 6.3, 7.4 Q4, 9.6 đã nhắc tới Secrets Manager). Bài này giải thích Secrets Manager là gì, cách dùng bằng CLI và từ code .NET, rotation, các sự cố hay gặp, rồi đưa ra **phương án** áp dụng vào RoomSplit.

> Giá là mức tham khảo `us-east-1` tại thời điểm viết (11/2026), luôn kiểm tra lại trên trang giá của AWS. Mục 10 là **phương án**, chưa làm gì trên AWS và chưa sửa code; chờ chọn phương án rồi mới viết walkthrough.

## Mục lục
1. [Vấn đề: mật khẩu của RoomSplit đang nằm ở đâu](#1-vấn-đề-mật-khẩu-của-roomsplit-đang-nằm-ở-đâu)
2. [Secrets Manager là gì](#2-secrets-manager-là-gì)
3. [Các khái niệm cơ bản](#3-các-khái-niệm-cơ-bản)
4. [So sánh với env file và SSM Parameter Store](#4-so-sánh-với-env-file-và-ssm-parameter-store)
5. [Thực hành bằng CLI](#5-thực-hành-bằng-cli)
6. [Ai được đọc secret: IAM và resource policy](#6-ai-được-đọc-secret-iam-và-resource-policy)
7. [App lấy secret bằng cách nào](#7-app-lấy-secret-bằng-cách-nào)
8. [Rotation: tự đổi mật khẩu](#8-rotation-tự-đổi-mật-khẩu)
9. [Edge case hay gặp và cách xử lý](#9-edge-case-hay-gặp-và-cách-xử-lý)
10. [Phương án áp dụng vào RoomSplit](#10-phương-án-áp-dụng-vào-roomsplit)
11. [Thí nghiệm](#11-thí-nghiệm)
12. [Bảng tra cứu nhanh](#12-bảng-tra-cứu-nhanh)

---

## 1. Vấn đề: mật khẩu của RoomSplit đang nằm ở đâu

| Secret | Môi trường | Đang nằm ở |
|---|---|---|
| Mật khẩu MySQL root | dev | `.env` (`MYSQL_ROOT_PASSWORD`), `appsettings.Development.json` |
| `MYSQL_ROOT_PASSWORD`, `MYSQL_PASSWORD`, `REDIS_PASSWORD` | staging/prod/lab | `envs/<env>.env` (git-ignored), copy lên EC2 bằng scp |
| Connection string đầy đủ | mọi nơi | biến môi trường `ConnectionStrings__DefaultConnection` của container `api` |

Cách này **đủ tốt cho lab** vì file đã git-ignored. Nhưng khi lên production nó có các điểm yếu:

| Điểm yếu | Ví dụ cụ thể |
|---|---|
| Mật khẩu nằm **trên đĩa** dạng chữ thường | Ai đọc được `/home/ec2-user/envs/aws-lab.env` (hoặc một bản snapshot EBS) là có mật khẩu DB |
| Lộ qua `docker inspect` | `docker inspect roomsplit-lab-api-1` in ra toàn bộ `Env`, gồm cả connection string |
| Không biết **ai đã đọc** | Không có log truy cập |
| Đổi mật khẩu là việc tay | Sửa file trên từng máy, restart, dễ quên một máy |
| Chia sẻ cho đồng đội | Gửi file qua chat/email, mỗi người giữ một bản |

Secrets Manager giải quyết các điểm này: secret nằm ở một chỗ, được mã hóa, đọc bằng **IAM** (không cần mật khẩu để lấy mật khẩu), mọi lần đọc đều ghi vào **CloudTrail**, và có thể tự đổi định kỳ.

---

## 2. Secrets Manager là gì

**AWS Secrets Manager** là "két sắt" có API: bạn lưu một chuỗi bí mật (thường là JSON), AWS mã hóa bằng **KMS**, và chỉ những danh tính IAM được phép mới gọi được `GetSecretValue` để lấy ra.

```
                 ┌────────────── AWS ──────────────┐
EC2 (IAM role) ──┼─► Secrets Manager ──► KMS decrypt│
  GetSecretValue │       │                         │
                 │       └─► CloudTrail: ai đọc, lúc nào
                 └─────────────────────────────────┘
```

Ba ý quan trọng nhất:
1. **Không có "mật khẩu để lấy mật khẩu".** Máy EC2 dùng **instance profile** (IAM role gắn vào máy, xem [iam-notes.md](iam-notes.md) mục 2.3), SDK tự lấy credential tạm từ metadata của máy.
2. **Secret có version.** Ghi giá trị mới không xóa giá trị cũ ngay; nhãn `AWSCURRENT`/`AWSPREVIOUS` cho biết đâu là bản đang dùng.
3. **Rotation**: Secrets Manager gọi một Lambda theo lịch để đổi mật khẩu ở DB **và** cập nhật secret cùng lúc.

---

## 3. Các khái niệm cơ bản

### 3.1 Secret
Một mục trong két, có **tên** (ví dụ `roomsplit/lab/runtime`) và giá trị:
- `SecretString`: chuỗi, tối đa 64 KB. Gần như luôn là JSON key/value:
  ```json
  { "username": "roomsplit_app", "password": "Xy7...", "host": "roomsplit-lab-db.xxxx.us-east-1.rds.amazonaws.com", "port": 3306 }
  ```
- `SecretBinary`: dữ liệu nhị phân (file chứng chỉ, key). Ít dùng.

Dấu `/` trong tên chỉ là quy ước đặt tên, không phải thư mục thật, nhưng rất có ích cho IAM: cho phép `roomsplit/lab/*` là cho phép cả "nhánh" đó.

### 3.2 ARN có đuôi ngẫu nhiên
```
arn:aws:secretsmanager:us-east-1:<ACCOUNT_ID>:secret:roomsplit/lab/runtime-AbC123
                                                                        ^^^^^^^ 6 ký tự AWS tự thêm
```
Đuôi này giúp phân biệt secret cũ đã xóa với secret mới cùng tên. Hệ quả khi viết IAM policy: phải dùng `roomsplit/lab/runtime-??????` (đúng 6 ký tự) hoặc `roomsplit/lab/*`, **không** viết `roomsplit/lab/runtime` trần (edge case 9.3).

### 3.3 Version và staging label
Mỗi lần ghi giá trị mới (`PutSecretValue`) tạo ra một **version** (`VersionId` dạng UUID). Version được gắn **nhãn**:

| Nhãn | Ý nghĩa |
|---|---|
| `AWSCURRENT` | Bản đang dùng. `GetSecretValue` không chỉ định gì thì trả bản này |
| `AWSPREVIOUS` | Bản ngay trước đó, giữ lại để quay lui |
| `AWSPENDING` | Bản đang được tạo trong lúc rotation, chưa chính thức |

```
PutSecretValue "pass-2"
  trước:  v1 [AWSCURRENT]
  sau:    v1 [AWSPREVIOUS]   v2 [AWSCURRENT]
```
Version không còn nhãn nào sẽ bị AWS dọn dần, nên không nên coi secret là nơi lưu lịch sử lâu dài.

### 3.4 Mã hóa bằng KMS
- Mặc định dùng key do AWS quản lý `aws/secretsmanager`: miễn phí, không phải làm gì, mọi IAM principal trong tài khoản có quyền `GetSecretValue` là giải mã được.
- Dùng **customer managed key** (CMK, khoảng 1 USD/tháng/key) khi cần: chia sẻ secret sang tài khoản khác, hoặc muốn thêm một lớp khóa (người đọc phải có cả `kms:Decrypt` trên key đó).

### 3.5 Xóa có thời gian hối hận
`DeleteSecret` không xóa ngay mà đặt lịch xóa sau **7–30 ngày** (mặc định 30). Trong thời gian đó secret không đọc được, nhưng `RestoreSecret` lấy lại được. Có `--force-delete-without-recovery` để xóa ngay (cẩn thận, edge case 9.4).

### 3.6 Các tính năng khác
| Tính năng | Là gì |
|---|---|
| **Resource policy** | Policy gắn vào chính secret (giống bucket policy), dùng khi cho tài khoản khác đọc |
| **Replication** | Sao secret sang Region khác (cho hệ thống multi-Region) |
| **Tags** | Gắn tag `Project=roomsplit`, `Env=lab` để lọc chi phí và viết policy theo tag (ABAC) |
| **Managed secret** | Secret do dịch vụ khác tạo và quản lý, ví dụ RDS tạo `rds!db-...` cho master user (mục 8.3) |

### 3.7 Chi phí
| Mục | Giá tham khảo |
|---|---|
| Mỗi secret | 0,40 USD/tháng (tính theo giờ, secret đang chờ xóa không tính) |
| API call | 0,05 USD / 10.000 lần gọi |
| KMS key `aws/secretsmanager` | Miễn phí |

Ví dụ lab: 1 secret JSON giữ 3 mật khẩu, app đọc 1 lần lúc khởi động → khoảng **0,40 USD/tháng**. Nếu app gọi `GetSecretValue` **mỗi request** với 1 triệu request/tháng → thêm 5 USD và có thể bị throttle (edge case 9.1). Vì vậy luôn **cache**.

---

## 4. So sánh với env file và SSM Parameter Store

**SSM Parameter Store** là dịch vụ "anh em" cũng lưu được giá trị bí mật (kiểu `SecureString`, mã hóa KMS). Câu hỏi hay gặp: dùng cái nào?

| Tiêu chí | Env file (hiện tại) | Parameter Store `SecureString` | Secrets Manager |
|---|---|---|---|
| Giá | 0 | Standard: **miễn phí** (tối đa 10.000 tham số, 4 KB/tham số) | 0,40 USD/secret/tháng + API |
| Mã hóa lúc lưu | Không (file chữ thường) | KMS | KMS |
| Đọc bằng IAM, có CloudTrail | Không | Có | Có |
| Version | Không | Có (số version, label tự đặt) | Có (`AWSCURRENT`/`AWSPREVIOUS`) |
| **Rotation tự động** | Không | **Không** (phải tự viết) | **Có**, có sẵn Lambda mẫu cho MySQL |
| Tích hợp RDS managed password | Không | Không | **Có** |
| Giới hạn tốc độ đọc | Không | Standard khá thấp; có thể bật throughput cao hơn (tính tiền) | Cao (hàng nghìn request/giây) |
| Chia sẻ sang tài khoản khác | Không | Hạn chế | Có (resource policy) |
| Hợp với | Lab, dev | Config + secret đơn giản, không cần rotation | Mật khẩu DB, API key cần rotation |

Quy tắc ngắn: **cấu hình thường** (`Cache:MembersTtlSeconds`, `App:TimeZone`) để ở appsettings/env hoặc Parameter Store `String`; **mật khẩu DB cần rotation** để ở Secrets Manager. Parameter Store `SecureString` là lựa chọn tiết kiệm khi chưa cần rotation. Parameter Store còn đọc được secret của Secrets Manager qua đường dẫn đặc biệt `/aws/reference/secretsmanager/<tên secret>`.

---

## 5. Thực hành bằng CLI

> Cần profile có quyền `secretsmanager:*` trên `roomsplit/*` (mục 10.4). Ví dụ dưới chạy trong **Git Bash**; trên PowerShell, JSON có dấu nháy rất dễ hỏng, nên luôn truyền qua file `file://`.

### 5.1 Tạo mật khẩu ngẫu nhiên và tạo secret
```bash
# Sinh mật khẩu 32 ký tự, bỏ dấu câu để không phá connection string (edge case 9.6)
aws secretsmanager get-random-password --password-length 32 --exclude-punctuation \
  --query RandomPassword --output text

# Viết JSON ra file tạm (để trong scratchpad, KHÔNG để trong repo), rồi tạo secret
cat > /tmp/runtime.json <<'EOF'
{ "mysqlRootPassword": "<pass-1>", "mysqlPassword": "<pass-2>", "redisPassword": "<pass-3>" }
EOF
aws secretsmanager create-secret \
  --name roomsplit/lab/runtime \
  --description "RoomSplit lab: passwords for mysql, redis" \
  --secret-string file:///tmp/runtime.json \
  --tags Key=Project,Value=roomsplit Key=Env,Value=lab
rm /tmp/runtime.json
```
Kết quả trả về `ARN` (có đuôi 6 ký tự), `Name`, `VersionId`.

### 5.2 Đọc
```bash
# Toàn bộ chuỗi JSON
aws secretsmanager get-secret-value --secret-id roomsplit/lab/runtime \
  --query SecretString --output text

# Lấy một key bằng jq
aws secretsmanager get-secret-value --secret-id roomsplit/lab/runtime \
  --query SecretString --output text | jq -r .mysqlPassword

# Xem metadata (KHÔNG có giá trị): version, nhãn, lịch rotation, ngày đọc gần nhất
aws secretsmanager describe-secret --secret-id roomsplit/lab/runtime
```
`describe-secret` có `LastAccessedDate` (làm tròn theo ngày): mẹo để biết secret có còn ai dùng không trước khi xóa.

### 5.3 Đổi giá trị và xem version
```bash
aws secretsmanager put-secret-value --secret-id roomsplit/lab/runtime \
  --secret-string file:///tmp/runtime-v2.json

aws secretsmanager list-secret-version-ids --secret-id roomsplit/lab/runtime
# → v1: ["AWSPREVIOUS"], v2: ["AWSCURRENT"]

# Đọc bản trước (quay lui khi bản mới sai)
aws secretsmanager get-secret-value --secret-id roomsplit/lab/runtime --version-stage AWSPREVIOUS
```
`put-secret-value` **thay cả chuỗi**, không merge JSON. Muốn đổi một key thì đọc ra, sửa, ghi lại toàn bộ.

### 5.4 Xóa và khôi phục
```bash
aws secretsmanager delete-secret --secret-id roomsplit/lab/runtime --recovery-window-in-days 7
aws secretsmanager restore-secret --secret-id roomsplit/lab/runtime   # trong 7 ngày đó
```

---

## 6. Ai được đọc secret: IAM và resource policy

### 6.1 Policy cho app (chỉ đọc đúng secret của nó)
Gắn vào **IAM role** của EC2 (instance profile), không gắn vào user:
```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ReadRoomSplitLabSecrets",
      "Effect": "Allow",
      "Action": "secretsmanager:GetSecretValue",
      "Resource": "arn:aws:secretsmanager:us-east-1:<ACCOUNT_ID>:secret:roomsplit/lab/*"
    }
  ]
}
```
- Chỉ `GetSecretValue`: app không cần `ListSecrets`, `DescribeSecret`, càng không cần `PutSecretValue`.
- Theo môi trường: role của prod chỉ được `roomsplit/prod/*`. Đây là lý do nên đặt tên có tầng `<app>/<env>/<name>`.
- Nếu secret mã hóa bằng CMK: thêm `kms:Decrypt` trên key đó (có thể kèm điều kiện `kms:ViaService = secretsmanager.us-east-1.amazonaws.com`).

### 6.2 Policy cho người quản trị secret
Ví dụ cho developer quản lý secret của dự án nhưng không đụng secret khác:
```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ManageRoomSplitSecrets",
      "Effect": "Allow",
      "Action": [
        "secretsmanager:CreateSecret", "secretsmanager:PutSecretValue", "secretsmanager:UpdateSecret",
        "secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret", "secretsmanager:ListSecretVersionIds",
        "secretsmanager:DeleteSecret", "secretsmanager:RestoreSecret", "secretsmanager:TagResource"
      ],
      "Resource": "arn:aws:secretsmanager:us-east-1:<ACCOUNT_ID>:secret:roomsplit/*"
    },
    {
      "Sid": "AccountLevelActions",
      "Effect": "Allow",
      "Action": ["secretsmanager:ListSecrets", "secretsmanager:GetRandomPassword"],
      "Resource": "*"
    }
  ]
}
```
`ListSecrets` và `GetRandomPassword` không gắn với secret cụ thể nên `Resource` phải là `*`.

### 6.3 Resource policy
Gắn thẳng vào secret, dùng chủ yếu để cho **tài khoản khác** đọc (khi đó bắt buộc dùng CMK, vì key `aws/secretsmanager` không chia sẻ được). Có thể dùng để **chặn** (Deny) mọi principal trừ role của app. Trong một tài khoản như lab thì không cần.

### 6.4 Credential của EC2 đi từ đâu tới SDK
```
Instance profile (role roomsplit-lab-app-role)
  → IMDSv2 http://169.254.169.254/latest/meta-data/iam/security-credentials/...
    → credential tạm (tự gia hạn)
      → AWS SDK trong container api (default credential chain)
```
Container Docker đọc IMDS qua thêm một "hop" mạng, nên máy phải có **`HttpPutResponseHopLimit` ≥ 2** (edge case 9.5). AMI Amazon Linux 2023 đặt sẵn 2.

---

## 7. App lấy secret bằng cách nào

Có bốn kiểu, từ đơn giản tới "chuẩn production":

| Kiểu | Cách làm | Sửa code | Theo kịp rotation | Secret có nằm trên đĩa/env |
|---|---|---|---|---|
| **A. Lấy lúc deploy** | Script trên EC2 gọi CLI, ghi env file tạm hoặc `export`, rồi `docker compose up` | Không | Không (phải redeploy) | Có (env của container) |
| **B. App đọc lúc khởi động** | AWS SDK trong `Program.cs`, nạp secret vào `IConfiguration` | Có | Nếu có reload | Không |
| **C. Secrets Manager Agent** | Tiến trình phụ (sidecar) cache secret, app gọi `http://localhost:2773/...` | Có (HTTP) | Có (TTL cache) | Không |
| **D. Nền tảng tiêm vào** | ECS task definition `secrets.valueFrom`, EKS CSI driver | Không | Khi task khởi động lại | Env của container |

### 7.1 Kiểu A: lấy lúc deploy (không sửa code)
```bash
#!/usr/bin/env bash
# scripts/load-secrets.sh (chạy trên EC2, credential từ instance profile)
set -euo pipefail
json=$(aws secretsmanager get-secret-value --secret-id roomsplit/lab/runtime \
         --query SecretString --output text)
export MYSQL_ROOT_PASSWORD=$(jq -r .mysqlRootPassword <<<"$json")
export MYSQL_PASSWORD=$(jq -r .mysqlPassword <<<"$json")
export REDIS_PASSWORD=$(jq -r .redisPassword <<<"$json")

docker compose -f docker-compose.yml -f docker-compose.prod.yml \
  --env-file envs/aws-lab.env up -d    # env file giờ không còn mật khẩu
```
Compose ưu tiên biến môi trường của shell hơn `--env-file`, nên `envs/aws-lab.env` chỉ còn giữ `COMPOSE_PROJECT_NAME`, `API_TAG`, `API_PORT`… Mật khẩu không còn nằm trên đĩa EC2, nhưng vẫn thấy được qua `docker inspect`.

### 7.2 Kiểu B: app đọc lúc khởi động (.NET)
Gói: `AWSSDK.SecretsManager` (và tùy chọn `AWSSDK.Extensions.NETCore.Setup`). Các gói AWS SDK **không phụ thuộc EF Core**, nên không đụng ràng buộc EF Core 9.0.x; vẫn thêm version vào `Directory.Packages.props`.

Ý tưởng: một **configuration provider** nhỏ, đọc secret JSON và coi mỗi key là một khóa cấu hình. Đặt tên key theo kiểu cấu hình .NET thì không phải map gì thêm:
```json
{
  "ConnectionStrings:DefaultConnection": "Server=...;User=roomsplit_app;Password=...;",
  "ConnectionStrings:Redis": "redis:6379,password=...,abortConnect=false"
}
```
```csharp
// Program.cs (phác thảo): chỉ bật khi có cấu hình, nên dev chạy `dotnet run` vẫn như cũ
var secretId = builder.Configuration["Aws:SecretsManager:SecretId"];
if (!string.IsNullOrWhiteSpace(secretId))
{
    builder.Configuration.AddSecretsManagerJson(secretId);   // provider tự viết, ~40 dòng
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw ...;
```
```csharp
// Phần lõi của provider
public override void Load()
{
    using var client = new AmazonSecretsManagerClient();          // credential + region từ môi trường
    var response = client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = _secretId })
                         .GetAwaiter().GetResult();
    Data = JsonSerializer.Deserialize<Dictionary<string, string?>>(response.SecretString)!
           .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}
```
- Thứ tự nguồn cấu hình: provider thêm **sau cùng** sẽ thắng, nên giá trị trong secret ghi đè env/appsettings.
- Lỗi khi không đọc được (thiếu quyền, sai tên) nên để **app dừng ngay lúc khởi động** (fail fast), giống cách `Program.cs` đang làm với `App:TimeZone`.
- Container chỉ còn cần `Aws__SecretsManager__SecretId=roomsplit/lab/runtime` và `AWS_REGION=us-east-1`, không còn mật khẩu nào trong env.

**Có thể dùng thư viện có sẵn thay vì tự viết** (ví dụ `Kralizek.Extensions.Configuration.AWSSecretsManager`, hoặc `Amazon.Extensions.Configuration.SystemsManager` đọc qua `/aws/reference/secretsmanager/...`). Tự viết thì ít phụ thuộc và hiểu rõ cơ chế; dùng thư viện thì có sẵn reload định kỳ.

### 7.3 Kiểu B nâng cao: theo kịp rotation
Hiện `Program.cs` đọc connection string **một lần** vào biến `connectionString`. Muốn đổi mật khẩu mà không restart:
1. Provider tự `Load()` lại định kỳ (ví dụ mỗi 5 phút) rồi gọi `OnReload()`.
2. `AddDbContext((sp, options) => ...)` đọc connection string **mỗi lần tạo options** từ `IConfiguration` thay vì biến chụp lúc khởi động. Options của DbContext mặc định là scoped, nên lambda chạy lại theo từng request.
3. MySqlConnector giữ **pool riêng cho mỗi connection string**: mật khẩu mới → pool mới; kết nối cũ trong pool cũ vẫn sống vì MySQL không cắt phiên đang mở khi đổi mật khẩu.
4. Lưới an toàn: `EnableRetryOnFailure()` không retry lỗi 1045 (sai mật khẩu), nên cần bắt 1045 → buộc reload secret → thử lại một lần.

Cách thường gặp hơn trên production là **alternating users** (mục 8.2): luôn có một user còn đúng mật khẩu nên app chỉ cần reload định kỳ, không cần bắt 1045.

### 7.4 Kiểu C: Secrets Manager Agent
Agent mã nguồn mở của AWS, chạy cạnh app (service riêng trong compose hoặc trên host), cache secret theo TTL (mặc định 300 giây):
```bash
curl -H "X-Aws-Parameters-Secrets-Token: $(cat /var/run/awssmatoken)" \
  "http://localhost:2773/secretsmanager/get?secretId=roomsplit/lab/runtime"
```
Header token bắt buộc để chống SSRF (một request bị lừa gọi tới localhost không đọc được secret). Lợi: app không cần AWS SDK, mọi ngôn ngữ dùng chung. Hại: thêm một tiến trình phải vận hành. Với một API .NET duy nhất như RoomSplit thì kiểu B gọn hơn.

### 7.5 Kiểu D: để nền tảng tiêm vào
Khi chuyển sang ECS (ngoài phạm vi hiện tại):
```json
"secrets": [
  { "name": "ConnectionStrings__DefaultConnection",
    "valueFrom": "arn:aws:secretsmanager:us-east-1:<ACCOUNT_ID>:secret:roomsplit/prod/db-AbC123:connectionString::" }
]
```
ECS đọc secret bằng **task execution role** lúc khởi động task, tiêm thành env. Code không đổi gì so với hiện tại. Rotation cần deploy lại task.

---

## 8. Rotation: tự đổi mật khẩu

### 8.1 Cơ chế: Lambda 4 bước
Khi bật rotation, Secrets Manager gọi một Lambda theo lịch (ví dụ `rate(30 days)` hoặc biểu thức cron), lần lượt với 4 `Step`:

| Bước | Lambda làm gì | Trạng thái secret |
|---|---|---|
| `createSecret` | Sinh mật khẩu mới, lưu thành version mới nhãn `AWSPENDING` | CURRENT = cũ, PENDING = mới |
| `setSecret` | Đăng nhập DB, `ALTER USER ... IDENTIFIED BY '<mới>'` | DB đã dùng mật khẩu mới |
| `testSecret` | Thử đăng nhập bằng `AWSPENDING` | |
| `finishSecret` | Chuyển nhãn `AWSCURRENT` sang version mới | CURRENT = mới, PREVIOUS = cũ |

AWS có sẵn **Lambda mẫu** cho MySQL (tạo từ Console chỉ vài cú nhấp), không phải tự viết.

**Khoảng nguy hiểm**: giữa `setSecret` và `finishSecret`, DB đã đổi mật khẩu nhưng `AWSCURRENT` vẫn là mật khẩu cũ. App mở kết nối mới trong khoảng này sẽ lỗi 1045. Đây là lý do có chiến lược thứ hai.

### 8.2 Single user và alternating users

| Chiến lược | Cách làm | Ưu | Nhược |
|---|---|---|---|
| **Single user** | Đổi mật khẩu của chính `roomsplit_app` | Đơn giản | Có khoảng vài giây lỗi đăng nhập (mục 8.1) |
| **Alternating users** | Có hai user `roomsplit_app` và `roomsplit_app_clone`; mỗi lần rotate chỉ đổi user **đang không dùng**, rồi chuyển `AWSCURRENT` sang user đó | Không bao giờ có lúc cả hai sai | Cần thêm một secret "superuser" để Lambda tạo/đổi user; hai user phải cùng quyền |

### 8.3 RDS managed master password
Khi tạo RDS có thể chọn **"Manage master credentials in AWS Secrets Manager"** (`--manage-master-user-password`). RDS tự tạo secret tên `rds!db-<uuid>` chứa `username` + `password`, tự rotate (mặc định 7 ngày), **không cần Lambda**, không cần mạng tới Secrets Manager từ VPC.
- Chỉ áp dụng cho **master user** (`admin`), không cho `roomsplit_app`/`roomsplit_migrator` (xem [aws-rds.md](aws-rds.md) mục 6.2).
- Rất hợp cho người quản trị: không ai phải "giữ" mật khẩu admin; khi cần thì đọc từ Secrets Manager.
- Cần quyền `secretsmanager:CreateSecret` (và KMS) cho người tạo RDS. `AmazonRDSFullAccess` của profile `dev-readonly` có thể không đủ, kiểm tra lúc làm.

### 8.4 Mạng cho rotation Lambda
Lambda rotation phải gọi được **cả** DB (trong VPC) **lẫn** API Secrets Manager (ngoài VPC). Nếu đặt Lambda trong subnet private không có NAT thì cần **VPC interface endpoint** `com.amazonaws.us-east-1.secretsmanager` (khoảng 0,01 USD/giờ/AZ ≈ 7,3 USD/tháng/AZ + 0,01 USD/GB). Lý do này khiến rotation tự động là bài "nâng cao" với lab.

---

## 9. Edge case hay gặp và cách xử lý

### 9.1 Gọi `GetSecretValue` mỗi request
- **Triệu chứng**: hóa đơn API tăng, thỉnh thoảng `ThrottlingException`, request chậm thêm 20–50 ms.
- **Xử lý**: đọc lúc khởi động hoặc cache có TTL (`AWSSDK.SecretsManager.Caching`, Agent, hoặc provider reload định kỳ). Không bao giờ đọc trong hot path.

### 9.2 `AccessDeniedException` dù đã gắn policy
Kiểm tra theo thứ tự:
1. Credential đang là ai? `aws sts get-caller-identity` (trên EC2: phải là role, không phải user).
2. ARN trong policy có khớp đuôi 6 ký tự không (9.3)?
3. Secret dùng CMK mà role thiếu `kms:Decrypt`?
4. Đúng Region chưa? Secret ở `us-east-1`, SDK gọi `ap-southeast-1` sẽ báo `ResourceNotFoundException`, không phải AccessDenied.
5. Có Deny nào trong resource policy của secret, SCP, permission boundary?

### 9.3 Policy viết ARN không có đuôi
`"Resource": "arn:...:secret:roomsplit/lab/runtime"` **không khớp** secret thật `roomsplit/lab/runtime-AbC123` → AccessDenied. Dùng `roomsplit/lab/runtime-??????`. Không nên dùng `roomsplit/lab/runtime*` vì nó khớp luôn `roomsplit/lab/runtime-old`, `roomsplit/lab/runtime2`…

### 9.4 Xóa rồi tạo lại cùng tên bị lỗi
- **Triệu chứng**: `InvalidRequestException: ... scheduled for deletion`.
- **Xử lý**: `restore-secret` rồi dùng tiếp, hoặc chờ hết recovery window, hoặc `delete-secret --force-delete-without-recovery` (không lấy lại được). Script IaC tạo/xóa lặp lại hay gặp lỗi này.

### 9.5 Container trên EC2 không lấy được credential
- **Triệu chứng**: SDK báo `Unable to get IAM security credentials from EC2 Instance Metadata Service` dù `aws sts get-caller-identity` trên host chạy được.
- **Nguyên nhân**: IMDSv2 với hop limit = 1; gói tin từ container đi qua bridge Docker là hop thứ 2.
- **Xử lý**: `aws ec2 modify-instance-metadata-options --instance-id <id> --http-put-response-hop-limit 2 --http-tokens required`.

### 9.6 Mật khẩu có ký tự đặc biệt phá connection string
- **Triệu chứng**: mật khẩu chứa `;` hoặc `=` → MySqlConnector đọc sai, báo lỗi đăng nhập hoặc "keyword not supported". Trong compose, `$` bị hiểu là biến.
- **Xử lý**: sinh mật khẩu bằng `--exclude-punctuation` (32 ký tự chữ + số đã đủ mạnh), hoặc ghép connection string bằng `MySqlConnectionStringBuilder` thay vì nối chuỗi.

### 9.7 Secret lộ ra log
- Connection string bị in khi log cấu hình lúc khởi động, khi `EnableSensitiveDataLogging()`, khi exception message chứa nó, hoặc qua `docker inspect`/`/proc/<pid>/environ`.
- **Xử lý**: không log `IConfiguration` nguyên khối; chỉ bật sensitive logging ở Development; kiểu B/C giữ mật khẩu ngoài env. CloudTrail **không** ghi giá trị secret, chỉ ghi ai đọc.

### 9.8 Rotation chạy nhưng app vẫn dùng mật khẩu cũ
Đã nói ở [aws-rds.md](aws-rds.md) mục 9.6. Tóm tắt: kiểu A/D cần redeploy sau mỗi lần rotate; kiểu B cần reload (7.3); kết hợp alternating users để không có khoảng lỗi.

### 9.9 Rotation Lambda timeout
- **Nguyên nhân**: Lambda trong VPC không tới được Secrets Manager (thiếu NAT/endpoint) hoặc không tới được DB (SG). Rotation dừng ở trạng thái có `AWSPENDING`, lần sau báo "rotation already in progress".
- **Xử lý**: sửa mạng (8.4), xem log Lambda trong CloudWatch, rồi `rotate-secret` lại. Không tự xóa nhãn `AWSPENDING` khi chưa hiểu bước nào hỏng.

### 9.10 App trong subnet private không gọi được Secrets Manager
- **Triệu chứng**: khởi động treo rồi timeout khi gọi `secretsmanager.us-east-1.amazonaws.com`.
- **Nguyên nhân**: subnet không có đường ra Internet (không NAT) và không có VPC endpoint. Đúng với `app-a` trong lab (mục 10.1).
- **Xử lý**: interface endpoint cho Secrets Manager (bật Private DNS để SDK không phải đổi URL), SG của endpoint cho phép 443 từ SG `app`.

### 9.11 Dev cũng đòi AWS
- **Triệu chứng**: dev mới clone repo, `dotnet run` lỗi vì không có credential AWS.
- **Xử lý**: chỉ bật đọc Secrets Manager khi có cấu hình `Aws:SecretsManager:SecretId`. Dev giữ `appsettings.Development.json` / user-secrets như hiện tại.

---

## 10. Phương án áp dụng vào RoomSplit

### 10.1 Hiện trạng (kiểm tra bằng CLI ngày 02/11/2026)
- Không có EC2, RDS nào đang chạy; VPC lab và EICE còn nguyên.
- Profile `dev-readonly` **không** có quyền `secretsmanager:*` và `ssm:*` (`ListSecrets`, `DescribeParameters` đều AccessDenied). Cũng không tạo được IAM role.
- Group `developers` đã gắn **10 managed policy**, đúng bằng quota mặc định "managed policies per group". Muốn thêm quyền Secrets Manager phải: gộp vào policy `roomsplit-lab-eice-connect` (thành một policy `roomsplit-lab-developer`), hoặc dùng inline policy, hoặc gỡ bớt policy thừa (ví dụ `AmazonS3ReadOnlyAccess` đã nằm trong `AmazonS3FullAccess`).
- Secret hiện có: 3 mật khẩu trong `envs/aws-lab.env` (mục 1). Dev dùng `appsettings.Development.json`, không đổi.
- Nếu đi theo R2/R3 của [aws-rds.md](aws-rds.md) (API trong `app-a`, không NAT), app **không gọi được** Secrets Manager nếu không có VPC endpoint (9.10). Nếu API ở public subnet như giai đoạn 2 thì gọi được.

### 10.2 Các quyết định

| # | Quyết định | Lựa chọn |
|---|---|---|
| S | App lấy secret thế nào | S0 / S1 / S2 / S3 bên dưới |
| T | Một secret JSON hay mỗi mật khẩu một secret | T1: một secret `roomsplit/<env>/runtime` (0,40 USD) / T2: mỗi mật khẩu một secret (1,20 USD, IAM chi tiết hơn) |
| K | Secrets Manager hay Parameter Store `SecureString` | Secrets Manager (học rotation, tích hợp RDS) / Parameter Store (miễn phí) |

### 10.3 Các phương án cho S

**S0. Giữ env file như hiện tại**
- Ưu: không tốn gì, không cần quyền mới.
- Nhược: không học thêm gì; vẫn có mọi điểm yếu ở mục 1.

**S1. Secrets Manager + lấy lúc deploy (kiểu A, mục 7.1)**
```
root: tạo role roomsplit-lab-app-role (GetSecretValue roomsplit/lab/*) + instance profile
dev:  create-secret roomsplit/lab/runtime
EC2:  scripts/load-secrets.sh → export → docker compose up
```
- Code .NET: **không sửa**. Thêm `scripts/load-secrets.sh`, bỏ 3 mật khẩu khỏi `envs/aws-lab.env.example`.
- Áp dụng được cho **cả** container `mysql` và `redis` (chúng đọc mật khẩu từ env, không biết AWS là gì).
- Ưu: học đúng phần cốt lõi (secret, IAM role, instance profile, CloudTrail) mà không đụng code; hỏng gì cũng dễ quay về env file.
- Nhược: mật khẩu vẫn nằm trong env của container; rotation cần redeploy.

**S2. App đọc Secrets Manager lúc khởi động (kiểu B, mục 7.2)**
- Code: configuration provider + `AWSSDK.SecretsManager` trong `Directory.Packages.props`; chỉ bật khi có `Aws:SecretsManager:SecretId`; cập nhật `appsettings.*.example.json`.
- Secret chứa `ConnectionStrings:DefaultConnection`, `ConnectionStrings:Redis`. Container `api` chỉ còn `Aws__SecretsManager__SecretId`.
- Ưu: không mật khẩu nào trong env của `api`; là nền để làm reload/rotation (7.3).
- Nhược: container `mysql`/`redis` vẫn cần mật khẩu dạng env, nên với compose hiện tại vẫn phải làm thêm S1 cho chúng. S2 chỉ "sạch" khi DB là RDS và Redis là ElastiCache (không còn container cần mật khẩu). Có thêm phụ thuộc AWS SDK cho code.

**S3. S2 + RDS managed master password + rotation app user (đầy đủ production)**
- RDS `--manage-master-user-password`; secret `roomsplit/lab/db-app` cho `roomsplit_app` rotate bằng Lambda mẫu (alternating users); app reload (7.3); VPC endpoint cho Secrets Manager.
- Ưu: không ai giữ mật khẩu; đổi định kỳ tự động.
- Nhược: nhiều thành phần mới cùng lúc (RDS, Lambda trong VPC, endpoint ~7,3 USD/tháng, IAM role cho Lambda cần root); khó gỡ lỗi khi mới học.

| | S0 | S1 | S2 | S3 |
|---|---|---|---|---|
| Sửa code .NET | Không | Không | Có | Có |
| Quyền mới cần root cấp | Không | Role + instance profile, quyền SM cho dev | Như S1 | Như S1 + role Lambda |
| Chi phí thêm/tháng | 0 | ~0,40 USD | ~0,40 USD | ~1 USD + endpoint ~7,3 USD |
| Mật khẩu trên đĩa EC2 | Có | Không | Không | Không |
| Mật khẩu trong env container | Có | Có | Không (`api`) | Không |
| Rotation | Tay | Tay + redeploy | Tay + restart (hoặc reload) | Tự động |
| Cần RDS | Không | Không | Không (nhưng chỉ "sạch" khi có) | Có |

### 10.4 Việc root phải làm trước (với S1/S2)
1. Policy cho `dev-readonly` quản lý `roomsplit/*` (mục 6.2): gộp vào policy customer managed hiện có hoặc dùng inline vì group đã đủ 10 managed policy.
2. Role `roomsplit-lab-app-role` (trust `ec2.amazonaws.com`) + policy đọc `roomsplit/lab/*` (mục 6.1) + instance profile cùng tên.
3. Cho `dev-readonly` quyền `iam:PassRole` **chỉ** trên role này, để tự gắn instance profile khi tạo EC2.

### 10.5 Đề xuất
**S1 + T1 + Secrets Manager**, làm cùng lúc với giai đoạn có EC2 tiếp theo (R1 hoặc giai đoạn 2 dựng lại); để **S2 → S3** cho khi đã có RDS.

Lý do:
- S1 dạy đúng 80% giá trị của Secrets Manager (lưu tập trung, IAM role thay mật khẩu, CloudTrail, version) mà **không sửa code**, và phủ được cả `mysql`/`redis` đang chạy bằng container, điều S2 không làm được.
- Code của RoomSplit đã đọc mọi thứ từ `IConfiguration`/env, nên khi chuyển sang S2 sau này chỉ là thêm một nguồn cấu hình, không phải viết lại.
- T1 (một secret JSON): rẻ nhất và một lần đọc là đủ; tách secret (T2) chỉ đáng khi các thành phần do người/role khác nhau đọc (ví dụ migrator khác app ở S3).
- Không chọn Parameter Store dù miễn phí, vì bước tiếp theo trong lộ trình là RDS + rotation, hai thứ chỉ Secrets Manager có sẵn. Nếu muốn 0 đồng, Parameter Store `SecureString` thay được ở S1 gần như y hệt (`aws ssm get-parameter --with-decryption`).
- Nếu chọn R2/R3 (API trong `app-a`) thì S1 cần thêm VPC endpoint hoặc chạy `load-secrets.sh` ở chỗ có Internet; khi đó nên cân nhắc lại.

---

## 11. Thí nghiệm
Làm được ngay khi có quyền (mục 10.4 bước 1), chưa cần EC2:

| # | Làm gì | Quan sát được |
|---|---|---|
| 1 | Tạo `roomsplit/lab/runtime` (5.1), đọc lại (5.2) | ARN có đuôi 6 ký tự |
| 2 | `put-secret-value` 2 lần, `list-secret-version-ids` | Nhãn `AWSCURRENT`/`AWSPREVIOUS` dịch chuyển |
| 3 | Viết policy ARN thiếu đuôi (9.3), thử đọc bằng role | AccessDenied |
| 4 | `delete-secret` với recovery 7 ngày, `create-secret` cùng tên | Lỗi "scheduled for deletion" (9.4); `restore-secret` |
| 5 | CloudTrail → Event history, lọc `GetSecretValue` | Ai đọc, lúc nào, từ IP nào; **không** có giá trị secret |
| 6 | Trên EC2 có instance profile, chạy `aws secretsmanager get-secret-value` trên host và trong container với hop limit 1 rồi 2 | Edge case 9.5 |

---

## 12. Bảng tra cứu nhanh
```bash
# Tạo / đọc / đổi
aws secretsmanager create-secret --name <name> --secret-string file://x.json
aws secretsmanager get-secret-value --secret-id <name> --query SecretString --output text
aws secretsmanager put-secret-value --secret-id <name> --secret-string file://x.json
aws secretsmanager describe-secret --secret-id <name>
aws secretsmanager list-secret-version-ids --secret-id <name>
aws secretsmanager get-random-password --password-length 32 --exclude-punctuation

# Xóa / khôi phục
aws secretsmanager delete-secret --secret-id <name> --recovery-window-in-days 7
aws secretsmanager restore-secret --secret-id <name>

# Rotation
aws secretsmanager rotate-secret --secret-id <name>                      # chạy ngay
aws secretsmanager cancel-rotation --secret-id <name>

# Gỡ lỗi
aws sts get-caller-identity
aws ec2 describe-instances --instance-ids <id> --query 'Reservations[].Instances[].MetadataOptions'
```

| Thuật ngữ | Nghĩa ngắn |
|---|---|
| `SecretString` | Giá trị dạng chuỗi (thường là JSON) |
| `AWSCURRENT` / `AWSPREVIOUS` / `AWSPENDING` | Bản đang dùng / bản trước / bản đang rotate |
| Recovery window | 7–30 ngày chờ trước khi xóa thật |
| Instance profile | Cái "vỏ" để gắn IAM role vào EC2 |
| Alternating users | Rotate luân phiên hai user DB để không có lúc mật khẩu sai |
| Managed secret | Secret do dịch vụ khác (RDS) tạo và tự rotate |
