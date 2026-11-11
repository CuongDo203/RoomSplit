# Cấu hình kết nối MySQL: chuỗi đầy đủ hay từng trường

Phân tích đề xuất: thay vì đọc cả `ConnectionStrings:DefaultConnection` từ config, đọc từng trường (host, port, database, user, password) rồi ghép thành connection string trong code.

> Trạng thái: **đã chọn phương án B** và đã triển khai (xem [mục 8](#8-đã-triển-khai-phương-án-b)). Mục 5 giữ lại khuyến nghị ban đầu (C) để tham khảo.

## 1. Hiện trạng

[Program.cs](../RoomSplit.Api/Program.cs):

```csharp
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
```

Giá trị đến từ:

| Môi trường | Nguồn | Giá trị |
|---|---|---|
| `dotnet run` (dev) | `appsettings.Development.json` (git-ignored) | `Server=localhost;Port=3307;Database=roomsplit;User=root;Password=...;` |
| compose dev | `docker-compose.override.yml` | `Server=mysql;Port=3306;...;User=root;Password=${MYSQL_ROOT_PASSWORD};` |
| compose staging/prod/lab | `docker-compose.prod.yml` | `Server=mysql;...;User=${MYSQL_USER};Password=${MYSQL_PASSWORD};` |
| AWS (kế hoạch) | Secrets Manager / ECS `secrets.valueFrom` | xem [aws-secrets-manager.md](aws-secrets-manager.md) |

Lưu ý: compose **đã** ghép chuỗi từ từng biến (`MYSQL_USER`, `MYSQL_PASSWORD`), chỉ là ghép bằng YAML chứ không phải bằng code.

## 2. Đọc cả chuỗi có vấn đề gì thật

| Vấn đề | Có thật không | Ghi chú |
|---|---|---|
| Password nằm lẫn trong chuỗi chứa cả host/port/db | Có | Muốn đổi mỗi host (ví dụ chuyển sang RDS) cũng phải chạm vào giá trị chứa password. Không tách được "config thường" và "secret" ra 2 nơi lưu khác nhau |
| Password có ký tự đặc biệt (`;`, `=`, `'`, `"`) làm hỏng chuỗi | Có | Ghép bằng YAML/chuỗi không escape. Password `ab;cd` → parser hiểu `cd` là một key lạ → lỗi lúc startup. `MySqlConnectionStringBuilder` tự quote đúng |
| Không khớp định dạng secret của RDS | Có | Secret RDS quản lý (`--manage-master-user-password`) là JSON `{"username","password"}`, secret do RDS sinh có thể kèm `host`, `port`. ECS `valueFrom` lấy được **từng key** (`...:password::`), không ghép được chuỗi. Rotation đổi `password` thì chuỗi ghép sẵn bị cũ |
| Validate từng trường (thiếu host, port sai) | Một phần | Hiện chỉ báo "không tìm thấy chuỗi"; chuỗi sai thì lỗi khi kết nối lần đầu, khó hiểu hơn |
| Bảo mật: password vẫn lộ qua `docker inspect` / env | **Không giải quyết được** | Tách trường không giấu được password: `Database__Password` vẫn là một biến môi trường. Muốn không lộ thì phải đọc từ file secret / Secrets Manager (bài khác) |

Điểm cộng của cách hiện tại (để công bằng): đây là quy ước chuẩn của .NET (`GetConnectionString`), mọi tài liệu/công cụ đều hiểu, thêm tùy chọn (`SslMode=Required`, `ConnectionTimeout=5`) không cần sửa code.

## 3. Phương án

### A. Giữ nguyên chuỗi đầy đủ
- Ưu: không sửa gì, chuẩn .NET, linh hoạt tùy chọn.
- Nhược: các vấn đề ở mục 2 còn nguyên; sang RDS managed secret phải tự ghép chuỗi ở đâu đó (script, entrypoint).

### B. Chỉ dùng từng trường, bỏ hẳn connection string
Section mới `Database`:

```json
"Database": {
  "Host": "localhost",
  "Port": 3307,
  "Name": "roomsplit",
  "User": "root",
  "Password": ""
}
```

Code (class `DatabaseOptions` + `ValidateOnStart`):

```csharp
var db = builder.Configuration.GetSection("Database").Get<DatabaseOptions>()!;
var connectionString = new MySqlConnectionStringBuilder
{
    Server = db.Host, Port = db.Port, Database = db.Name,
    UserID = db.User, Password = db.Password,
}.ConnectionString;
```

Compose: `Database__Host: mysql`, `Database__Password: ${MYSQL_PASSWORD}`, ...

- Ưu: rõ ràng, mỗi trường một biến; escape đúng; khớp secret RDS từng key; validate từng trường (thiếu `Host` → lỗi rõ khi start).
- Nhược: muốn thêm `SslMode` (RDS cần TLS) hay timeout thì phải thêm property + sửa code mỗi lần; phá quy ước `ConnectionStrings` nên các doc docker/AWS đang dùng `ConnectionStrings__DefaultConnection` phải sửa hết.

### C. Lai: chuỗi gốc (không password) + từng trường ghi đè
Vẫn đọc `ConnectionStrings:DefaultConnection` làm nền (có thể bỏ trống), sau đó trường nào có trong section `Database` thì **ghi đè** lên nền bằng `MySqlConnectionStringBuilder`:

```csharp
var csb = new MySqlConnectionStringBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection") ?? "");
var db = builder.Configuration.GetSection("Database");
if (db["Host"] is { Length: > 0 } host) csb.Server = host;
if (db.GetValue<uint?>("Port") is { } port) csb.Port = port;
if (db["Name"] is { Length: > 0 } name) csb.Database = name;
if (db["User"] is { Length: > 0 } user) csb.UserID = user;
if (db["Password"] is { } pwd) csb.Password = pwd;   // cho phép rỗng (dev)
if (string.IsNullOrEmpty(csb.Server)) throw new InvalidOperationException("Database host is not configured.");
var connectionString = csb.ConnectionString;
```

Dùng điển hình:

```yaml
# docker-compose.prod.yml
ConnectionStrings__DefaultConnection: "Server=mysql;Port=3306;Database=roomsplit;SslMode=None;"
Database__User: ${MYSQL_USER}
Database__Password: ${MYSQL_PASSWORD}
```

- Ưu: tách được secret (password, user) khỏi config thường; escape đúng; tùy chọn lạ (`SslMode`, timeout) vẫn viết vào chuỗi nền, không sửa code; chuỗi cũ có password vẫn chạy (tương thích ngược, đổi dần từng môi trường); khớp ECS `valueFrom` từng key của secret RDS.
- Nhược: hai nguồn cho cùng một giá trị, phải nhớ quy tắc **trường riêng thắng chuỗi nền**; code dài hơn B một chút.

## 4. So sánh

| Tiêu chí | A | B | C |
|---|---|---|---|
| Tách password khỏi config thường | ✗ | ✓ | ✓ |
| Password có ký tự đặc biệt | ✗ | ✓ | ✓ |
| Khớp secret RDS / ECS `valueFrom` từng key | ✗ | ✓ | ✓ |
| Thêm tùy chọn (SslMode…) không sửa code | ✓ | ✗ | ✓ |
| Tương thích cấu hình hiện có | ✓ | ✗ | ✓ |
| Đơn giản, một nguồn sự thật | ✓ | ✓ | ~ |
| Giấu password khỏi env / `docker inspect` | ✗ | ✗ | ✗ |

## 5. Khuyến nghị: C

- Đạt được điều bạn muốn (password và user thành trường riêng, ghép bằng builder an toàn) mà không mất khả năng thêm tùy chọn kết nối, điều sẽ cần ngay khi sang RDS (TLS).
- Đổi được từng bước: môi trường nào chưa sửa vẫn chạy với chuỗi đầy đủ.
- Nhớ rõ: tách trường **không** phải biện pháp bảo mật chính. Password vẫn ở env. Bước bảo mật thật là đọc từ Secrets Manager / file secret ([aws-secrets-manager.md](aws-secrets-manager.md)); C làm bước đó dễ hơn vì chỉ cần cung cấp `Database:Password`.

Redis (`ConnectionStrings:Redis`) giữ nguyên trong lần này (định dạng StackExchange.Redis khác, password đã tách sẵn qua `REDIS_PASSWORD` ở compose). Có thể áp dụng cùng ý tưởng sau.

## 6. Edge case

| Trường hợp | Xử lý |
|---|---|
| Không có cả chuỗi nền lẫn `Database:Host` | Throw lúc startup: `Database host is not configured.` |
| `Database:Port` không phải số | `GetValue<uint?>` throw lúc startup (fail fast) |
| Password rỗng (MySQL dev không mật khẩu) | Cho phép: chỉ bỏ qua khi key **không tồn tại**, không bỏ qua chuỗi rỗng |
| Password chứa `;` `=` `"` | Builder tự quote; thêm 1 test cho trường hợp này khi có test project |
| Log chuỗi kết nối để debug | Không bao giờ log chuỗi đã ghép; nếu cần thì log `csb.Server`, `csb.Database`, `csb.UserID` |
| `dotnet ef` (design-time) | Chạy qua `Program.cs` nên dùng cùng logic, không cần sửa gì |

## 7. Ảnh hưởng nếu chọn C

| File | Thay đổi |
|---|---|
| `RoomSplit.Api/Program.cs` | Ghép chuỗi bằng `MySqlConnectionStringBuilder` (có thể tách ra `Data/DatabaseConnection.cs` cho gọn) |
| `appsettings.Development.example.json` | Chuỗi nền không password + section `Database` (`Password` rỗng) |
| `appsettings.Development.json` (máy bạn) | Tương tự, tự sửa (git-ignored) |
| `docker-compose.override.yml`, `docker-compose.prod.yml` | Chuỗi nền không user/password + `Database__User`, `Database__Password` |
| `envs/*.env.example` | Không đổi (vẫn `MYSQL_USER`, `MYSQL_PASSWORD`) |
| Docs AWS (`aws-secrets-manager.md`, `aws-ecs-fargate-rds.md`) | Ghi chú: có thể tiêm `Database__Password` từ key `password` của secret |
| `CLAUDE.md` | Ghi quy ước mới ở mục 10/11 |

## 8. Đã triển khai: phương án B

### 8.1 Cấu hình

Section `Database` (dùng chung với `Database:MigrateOnStartup` có sẵn):

| Key | Env var | Bắt buộc | Mặc định |
|---|---|---|---|
| `Database:Host` | `Database__Host` | Có | |
| `Database:Port` | `Database__Port` | Không (1–65535) | `3306` |
| `Database:Name` | `Database__Name` | Có | |
| `Database:User` | `Database__User` | Có | |
| `Database:Password` | `Database__Password` | Không (cho phép rỗng) | `""` |

`ConnectionStrings:DefaultConnection` **không còn được đọc**. `ConnectionStrings:Redis` giữ nguyên.

### 8.2 Code

- [Common/DatabaseOptions.cs](../RoomSplit.Api/Common/DatabaseOptions.cs): options class + DataAnnotations; `BuildConnectionString()` ghép bằng `MySqlConnectionStringBuilder` (MySqlConnector, gói Pomelo kéo theo).
- [Program.cs](../RoomSplit.Api/Program.cs): `AddOptions<DatabaseOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`; trong `AddDbContext` lấy `IOptions<DatabaseOptions>` để ghép chuỗi. Thiếu trường → `OptionsValidationException` liệt kê đúng trường thiếu, ngay khi migrate lúc startup hoặc khi app start.
- Compose (`docker-compose.override.yml`, `docker-compose.prod.yml`): `Database__Host: mysql`, `Database__User`, `Database__Password` lấy từ `MYSQL_*` như trước. `envs/*.env` không đổi.
- `appsettings.Development.example.json`: section `Database` thay cho `DefaultConnection`. Mỗi dev tự sửa `appsettings.Development.json` của mình theo file mẫu.

### 8.3 Đã kiểm tra

| Kiểm tra | Kết quả |
|---|---|
| `dotnet run` (Development, MySQL compose cổng 3307) | `/health/ready` = `Healthy`, `GET /api/v1/rooms` trả dữ liệu |
| Thiếu Host/Name/User | Dừng lúc start: `DataAnnotation validation failed for 'DatabaseOptions' members: 'Host' ...` |
| Password `ab;c"d=e` | MySQL trả `Access denied ... (using password: YES)`, tức chuỗi được escape đúng (cách cũ sẽ lỗi parse) |
| `docker compose config` (dev) | Ra đủ `Database__*` |

### 8.4 Đổi tên khi đọc các doc cũ

Các bài docker/AWS viết trước thay đổi này (`docker.md`, `docker-compose.md`, `docker-multi-env.md`, `docker-debugging.md`, `docker-volumes-networks.md`, `aws-rds.md`, `aws-ecs-fargate-rds.md`, `aws-secrets-manager.md`, `aws-networking.md`) còn dùng `ConnectionStrings__DefaultConnection`. Khi làm theo, đổi thành:

| Cũ | Mới |
|---|---|
| `-e ConnectionStrings__DefaultConnection="Server=H;Port=P;Database=D;User=U;Password=X;"` | `-e Database__Host=H -e Database__Port=P -e Database__Name=D -e Database__User=U -e Database__Password=X` |
| ECS `secrets`: một key `connectionString` | Mỗi key riêng, ví dụ `Database__Password` ← `arn:...:secret:<name>:password::`, `Database__User` ← `...:username::`; Host/Port/Name để ở `environment` thường |
| Secrets Manager JSON `{"ConnectionStrings:DefaultConnection": "..."}` | `{"Database:User": "...", "Database:Password": "..."}` |
| Đổi sang RDS: sửa `Server=` trong chuỗi | Chỉ đổi `Database__Host` |

Hạn chế đã biết của B: cần tùy chọn kết nối mới (ví dụ `SslMode=Required` khi dùng RDS) thì thêm property vào `DatabaseOptions` và gán trong `BuildConnectionString()`.
