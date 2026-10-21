# P1: Mô phỏng 3 môi trường dev / staging / production bằng docker compose

Thiết kế chi tiết cho phương án **P1** trong [docker-environments.md](docker-environments.md#12-phương-án-áp-dụng-cho-roomsplit) (đã chọn 2026-10-21).

Mục tiêu: trên **cùng một máy**, chạy được 3 môi trường **song song và tách biệt**, dùng **cùng một image** cho staging và production, chỉ khác cấu hình.

> **Trạng thái:** đã triển khai và kiểm tra 2026-10-21. Kết quả ở [mục 6](#6-kết-quả-triển-khai). Hướng dẫn chạy nhanh ở [mục 4](#4-quy-trình-chạy-staging-và-production).

---

## 1. Kết quả mong muốn

| | Dev | Staging | Production |
|---|---|---|---|
| Lệnh | `docker compose up -d --build` | `docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/staging.env up -d` | như staging, với `envs/production.env` |
| Image API | Build từ source, tag `dev` | `roomsplit-api:<git-sha>` có sẵn, **không build** | **Cùng** `roomsplit-api:<git-sha>` với staging |
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Staging` | `Production` |
| Swagger | Bật | Bật (để QA thử) | Tắt |
| API tại | `localhost:8080` | `localhost:8081` | `localhost:8082` |
| Cổng MySQL ra máy host | `3307` (DBeaver, `dotnet ef`) | **Không mở** | **Không mở** |
| User DB | `root` | `roomsplit_app` (user riêng) | `roomsplit_app` (password khác staging) |
| Compose project / volume | `roomsplit` / `roomsplit_mysql-data` (**giữ nguyên dữ liệu dev hiện tại**) | `roomsplit-staging` / `roomsplit-staging_mysql-data` | `roomsplit-prod` / `roomsplit-prod_mysql-data` |
| Giới hạn tài nguyên, log rotation | Không | Có | Có |

> Production thật sẽ chạy trên server, sau reverse proxy HTTPS. Ở P1 ta mô phỏng trên máy local, nên vẫn mở cổng 8082 để gọi thử.

---

## 2. Các quyết định thiết kế bên trong P1

Đây là những lựa chọn nhỏ, mình chọn theo hướng đơn giản và an toàn cho dữ liệu hiện có. Muốn đổi chỗ nào thì sửa lại được dễ dàng.

| # | Quyết định | Chọn | Lý do | Phương án khác |
|---|---|---|---|---|
| 1 | Tên file compose | Giữ `docker-compose.yml`, thêm `docker-compose.override.yml`, `docker-compose.prod.yml` | Không phải đổi tên file đang dùng, README và tài liệu cũ vẫn đúng. Compose tự đọc `docker-compose.override.yml` giống như `compose.override.yaml` | Đổi sang `compose.yaml` (tên mới theo Compose Spec). Lợi ích nhỏ, phải sửa nhiều chỗ |
| 2 | Staging và production dùng chung một file override | Một file `docker-compose.prod.yml` + hai file env | Staging phải **giống** production (dev/prod parity). Khác nhau chỉ nằm trong file env | Hai file `docker-compose.staging.yml` và `docker-compose.prod.yml`: dễ lệch nhau theo thời gian |
| 3 | Tách biệt các môi trường trên cùng máy | `COMPOSE_PROJECT_NAME` trong mỗi file env | Project khác nhau thì network, volume, tên container đều khác. **Volume dev giữ nguyên tên `roomsplit_mysql-data`** | Truyền `-p` mỗi lần chạy: dễ quên, dễ chạy nhầm vào project dev |
| 4 | `container_name` | Chỉ đặt trong override dev (`roomsplit-mysql`, `roomsplit-api`) | Tên cố định ở file chung sẽ làm **trùng tên** giữa các môi trường. Dev giữ tên cũ để các lệnh đã quen vẫn dùng được | Bỏ hẳn `container_name`: phải gõ `docker compose exec` thay vì `docker exec` |
| 5 | Cổng | Không khai báo `ports` trong file chung, mỗi file override tự khai báo | Compose **cộng dồn** (append) danh sách `ports` khi merge. Khai báo ở file chung thì production cũng bị mở cổng | |
| 6 | Bật Swagger | `Swagger:Enabled` trong cấu hình. Không khai báo thì mặc định bật khi `IsDevelopment()` | Dev không cần sửa `appsettings.Development.json` (file git-ignored của từng người). Staging bật, production tắt qua `appsettings.{Env}.json` | Bắt buộc khai báo ở mọi môi trường: phải sửa file Development của mọi developer |
| 7 | Tạo schema cho DB staging/prod (DB mới, rỗng) | Script SQL idempotent (`dotnet ef migrations script --idempotent`) đưa vào MySQL qua `docker compose exec` | Không cần mở cổng DB. Script xem trước được, chạy lại không hỏng. Migration bundle (D3) để dành cho P3 | Mở tạm cổng DB rồi `dotnet ef database update`: trái với mục tiêu không mở cổng |
| 8 | Cố định phiên bản image | `mysql:8.0.46`, `sdk:10.0.402`, `aspnet:10.0.13` (đúng các bản đang chạy) | Mọi môi trường dùng **cùng phiên bản**. Pull ở ngày khác cũng không lệch | Giữ tag trôi `8.0`/`10.0`: tự có bản vá nhưng các môi trường có thể lệch nhau |
| 9 | Giới hạn tài nguyên | API 512 MB / 1 CPU, MySQL 1 GB / 1 CPU. Log `json-file` 10 MB × 3 file | Đủ cho MVP, không chiếm hết máy | |

---

## 3. Thiết kế từng file

### 3.1. `docker-compose.yml`: phần chung cho mọi môi trường

```yaml
services:
  mysql:
    image: mysql:8.0.46
    restart: unless-stopped
    environment:
      MYSQL_ROOT_PASSWORD: ${MYSQL_ROOT_PASSWORD:?Set MYSQL_ROOT_PASSWORD in the env file}
      MYSQL_DATABASE: roomsplit
      TZ: Asia/Ho_Chi_Minh
    command: --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci
    volumes:
      - mysql-data:/var/lib/mysql
    healthcheck: ...  # giữ nguyên

  api:
    image: roomsplit-api:${API_TAG:-dev}
    restart: unless-stopped
    depends_on:
      mysql:
        condition: service_healthy

volumes:
  mysql-data:
```

Không có `ports`, `container_name`, `build`, `ASPNETCORE_ENVIRONMENT` hay connection string. Những thứ này khác nhau theo môi trường.

### 3.2. `docker-compose.override.yml`: dev (compose tự đọc)

```yaml
services:
  mysql:
    container_name: roomsplit-mysql
    ports:
      - "${MYSQL_PORT:-3306}:3306"

  api:
    build: .
    container_name: roomsplit-api
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Server=mysql;Port=3306;Database=roomsplit;User=root;Password=${MYSQL_ROOT_PASSWORD};"
    ports:
      - "${API_PORT:-8080}:8080"
```

### 3.3. `docker-compose.prod.yml`: staging và production

```yaml
services:
  mysql:
    environment:
      MYSQL_USER: ${MYSQL_USER:?...}
      MYSQL_PASSWORD: ${MYSQL_PASSWORD:?...}
    deploy:
      resources:
        limits: { cpus: "1", memory: 1g }
    logging: &default-logging
      driver: json-file
      options: { max-size: "10m", max-file: "3" }

  api:
    environment:
      ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:?...}
      ConnectionStrings__DefaultConnection: "Server=mysql;Port=3306;Database=roomsplit;User=${MYSQL_USER};Password=${MYSQL_PASSWORD};"
    ports:
      - "${API_PORT:?...}:8080"
    deploy:
      resources:
        limits: { cpus: "1", memory: 512m }
    logging: *default-logging
```

- **Không có `build`**: image phải có sẵn, đúng với nguyên tắc "build một lần".
- **Không có `ports` cho mysql**: DB chỉ truy cập được từ network nội bộ.
- `MYSQL_USER`/`MYSQL_PASSWORD`: image MySQL tạo user này **lần đầu** khi volume rỗng, và cấp toàn quyền trên database `roomsplit` (không phải root của cả server).
- `&default-logging` / `*default-logging` là **YAML anchor**: khai báo một lần, dùng lại nhiều chỗ.

### 3.4. File env cho staging và production

```
envs/
├── staging.env.example      ← commit (không có password)
├── production.env.example   ← commit
├── staging.env              ← git-ignored, copy từ example rồi điền password
└── production.env           ← git-ignored
```

```dotenv
# envs/staging.env.example
COMPOSE_PROJECT_NAME=roomsplit-staging
ASPNETCORE_ENVIRONMENT=Staging
API_TAG=
API_PORT=8081
MYSQL_ROOT_PASSWORD=
MYSQL_USER=roomsplit_app
MYSQL_PASSWORD=
```

`production.env.example` giống hệt, chỉ khác `COMPOSE_PROJECT_NAME=roomsplit-prod`, `ASPNETCORE_ENVIRONMENT=Production`, `API_PORT=8082`. Mỗi môi trường **phải dùng password khác nhau**.

### 3.5. Cấu hình ASP.NET Core

| File | Nội dung | Vào image? |
|---|---|---|
| `appsettings.json` | Không đổi | ✅ |
| `appsettings.Staging.json` (mới) | `Swagger:Enabled: true` | ✅ (không bí mật) |
| `appsettings.Production.json` (mới) | `Swagger:Enabled: false`, log mặc định `Warning` (giữ `Microsoft.Hosting.Lifetime: Information` để thấy log khởi động) | ✅ (không bí mật) |

`Program.cs`:

```csharp
// Swagger is on in Development unless configured; other environments opt in via Swagger:Enabled.
if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
```

### 3.6. Các file khác

| File | Thay đổi |
|---|---|
| `Dockerfile` | Cố định `sdk:10.0.402`, `aspnet:10.0.13` |
| `.gitignore` | Thêm `envs/*.env` |
| `.dockerignore` | Thêm `envs/`, `docker-compose*.yml` |
| `README.md` | Mục Docker: 3 môi trường |
| `docs/docker-compose.md` | Ghi chú: file compose đã được tách (xem tài liệu này) |

---

## 4. Quy trình chạy staging và production

```powershell
# 1. Build image MỘT LẦN, tag theo commit hiện tại
$tag = git rev-parse --short HEAD
docker build -t roomsplit-api:$tag .

# 2. Tạo file env (lần đầu), đặt API_TAG=$tag và password riêng cho từng môi trường
Copy-Item envs/staging.env.example envs/staging.env
Copy-Item envs/production.env.example envs/production.env

# 3. Chạy staging
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/staging.env up -d

# 4. Tạo schema (lần đầu hoặc khi có migration mới), từ RoomSplit.Api/
dotnet ef migrations script --idempotent -o ../artifacts/migrate.sql
Get-Content ../artifacts/migrate.sql -Raw | docker compose -f ../docker-compose.yml -f ../docker-compose.prod.yml --env-file ../envs/staging.env exec -T mysql sh -c 'mysql -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" roomsplit'

# 5. Kiểm tra
curl http://localhost:8081/health/ready

# 6. Staging ổn → production chạy ĐÚNG image đó (cùng API_TAG)
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/production.env up -d
```

`artifacts/` đã có sẵn trong `.gitignore`.

---

## 5. Kiểm tra (định nghĩa "xong")

| # | Kiểm tra | Kỳ vọng |
|---|---|---|
| 1 | `docker compose config` cho từng môi trường | Hợp lệ. Prod/staging: mysql **không** có `ports`, api **không** có `build` |
| 2 | Dev: `docker compose up -d --build` | Vẫn dùng volume `roomsplit_mysql-data` cũ, dữ liệu cũ còn nguyên, Swagger bật |
| 3 | Staging và production chạy **song song** với dev | 6 container, 3 network, 3 volume riêng biệt |
| 4 | Image staging và production | Cùng image ID |
| 5 | Schema được tạo bằng script idempotent. Chạy script lần hai | Không lỗi |
| 6 | `/health/ready`, `/api/v1/categories` ở cả 3 cổng | `Healthy`, 6 category |
| 7 | Swagger | 8080 bật, 8081 bật, 8082 **404** |
| 8 | API staging/prod kết nối DB bằng `roomsplit_app`, không phải root | `information_schema.processlist` chỉ có user `roomsplit_app` trên database `roomsplit` |
| 9 | Cổng MySQL của staging/prod không mở ra host | `docker compose ps` không có cổng 3306 nào ra ngoài |
| 10 | Giới hạn tài nguyên | `docker stats` hiện giới hạn 512 MiB / 1 GiB |
| 11 | `dotnet format` và build Release | Không lỗi |

---

## 6. Kết quả triển khai

Các file được triển khai đúng như thiết kế ở mục 3. Local, mỗi file `envs/*.env` được tạo với password ngẫu nhiên (khác nhau giữa staging và production), và `API_TAG=6f82755-dirty`. Tag này lấy từ `git describe --always --dirty`: hậu tố `-dirty` cho biết image được build từ code **chưa commit**. Khi làm thật thì chỉ build từ commit sạch.

| # | Kiểm tra | Kết quả |
|---|---|---|
| 1 | `docker compose config` | ✅ Dev: project `roomsplit`, api có `build`, mysql mở 3307. Staging/prod: project `roomsplit-staging` / `roomsplit-prod`, **không** `build`, mysql **không** có cổng, giới hạn 512 MiB / 1 GiB |
| 2 | Dev `up -d --build` | ✅ Container dev được **tạo lại** (vì image mysql đổi tag `8.0` → `8.0.46`), volume `roomsplit_mysql-data` giữ nguyên, dữ liệu cũ (1 room, 1 member) còn đủ |
| 3 | Ba môi trường song song | ✅ 6 container, 3 network, 3 volume (`roomsplit_`, `roomsplit-staging_`, `roomsplit-prod_`) |
| 4 | Image staging và production | ✅ Cùng image ID `a42735877a9a` |
| 5 | Script idempotent, chạy 2 lần mỗi môi trường | ✅ Cả hai lần `exit=0`. `__EFMigrationsHistory` có đủ 2 migration, 6 category seed |
| 6 | `/health/ready`, `/api/v1/categories` | ✅ Cả 3 cổng: `Healthy`, 6 category |
| 7 | Swagger | ✅ 8080 → 200, 8081 → 200, 8082 → **404** |
| 8 | User DB của API staging | ✅ Processlist chỉ có `roomsplit_app` |
| 9 | Cổng MySQL staging/prod | ✅ `docker ps` chỉ hiện `3306/tcp` (nội bộ), không có `0.0.0.0:...->3306` |
| 10 | Giới hạn tài nguyên | ✅ API `~58 MiB / 512 MiB`, MySQL `~400 MiB / 1 GiB`. Dev không giới hạn (`/ 7.4 GiB`) |
| 11 | `dotnet format --verify-no-changes`, build Release | ✅ 0 lỗi |

**Quan sát:**
- Image `roomsplit-api:dev` (do compose build) có ID **khác** `roomsplit-api:6f82755-dirty`, dù cùng source code. Lý do: compose gắn thêm label (`com.docker.compose.*`) vào image nó build, nên metadata khác và ID khác. Đây chính là ví dụ cho nguyên tắc **"build một lần"**: so sánh bằng image ID mới chắc chắn hai môi trường chạy cùng một thứ. Staging và production có cùng ID, còn dev thì không cần.
- MySQL trên volume mới báo `healthy` rất nhanh (~10 giây), có thể trong lúc entrypoint còn đang khởi tạo. Điều này không gây lỗi, vì API chỉ kết nối khi có request.

### Dọn dẹp môi trường mô phỏng

```powershell
# Dừng staging/prod nhưng giữ dữ liệu
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/staging.env down
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/production.env down

# Xóa hẳn dữ liệu staging (cẩn thận: --env-file quyết định project nào bị xóa)
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/staging.env down -v
```

> ⚠️ Lệnh `down -v` **không** có `--env-file` sẽ nhắm vào project **dev** (`roomsplit`) và xóa dữ liệu dev.
