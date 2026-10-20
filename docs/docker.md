# Đóng gói RoomSplit.Api bằng Docker: phân tích và phương án

Phạm vi: **bước 1**, viết `Dockerfile` và `.dockerignore` để build được image của API và chạy nó trong container.
Bước 2–3 (kết nối MySQL, Docker network, docker-compose) sẽ có tài liệu riêng.

Kiến thức nền: xem [docker-basics.md](docker-basics.md).

> **Trạng thái:** đã chọn **A1 + B1 + C1** (2026-10-21) và đã triển khai. Kết quả kiểm tra ở [mục 7](#7-kết-quả-triển-khai).

---

## 1. Yêu cầu

- `docker build` tạo ra image `roomsplit-api` chạy được API.
- Dockerfile hai stage: build bằng `dotnet/sdk:10.0`, chạy bằng `dotnet/aspnet:10.0`.
- Restore trước khi copy source, để layer restore được cache.
- `.dockerignore` loại `bin/`, `obj/`, `.git`, `.env` và các file chứa bí mật.
- Không có bí mật nào nằm trong image.

## 2. Hiện trạng repo ảnh hưởng tới Dockerfile

| Hiện trạng | Hệ quả cho Dockerfile |
|---|---|
| Central Package Management: version nằm trong `Directory.Packages.props` | Bước restore phải copy cả `Directory.Packages.props`, không chỉ `.csproj` |
| `Directory.Build.props` chứa `TargetFramework` | Cũng phải copy trước bước restore |
| Hai file `.props` nằm ở **thư mục gốc** repo | Build context phải là **thư mục gốc**, không thể là `RoomSplit.Api/` |
| Release bật `TreatWarningsAsErrors` | Build trong Docker có cùng kỷ luật với CI: có warning là build lỗi |
| Connection string nằm trong `appsettings.Development.json` (git-ignored, chứa password) | File này **không** được vào image. Connection string truyền qua biến môi trường `ConnectionStrings__DefaultConnection` lúc `docker run` |
| `Program.cs` throw nếu thiếu connection string | Luôn phải truyền biến môi trường trên khi chạy container, kể cả khi chỉ thử `/health/live` |
| Swagger chỉ bật khi `Development` | Image chạy `Production` theo mặc định nên không có Swagger. Muốn xem thì thêm `-e ASPNETCORE_ENVIRONMENT=Development` |
| `App:TimeZone` = `Asia/Ho_Chi_Minh`, app fail-fast nếu không tìm thấy | Image runtime phải có **tzdata**. Đã kiểm tra: image `aspnet:10.0` (Ubuntu 24.04) có `/usr/share/zoneinfo/Asia/Ho_Chi_Minh` |
| `app.UseHttpsRedirection()` | Container chỉ có HTTP (cổng 8080), nên middleware sẽ log một warning "Failed to determine the https port" và không redirect. Chấp nhận được: HTTPS thường do reverse proxy đảm nhận. Không sửa ở bước này |
| Migration chưa tự chạy khi app khởi động | Container **không** tự tạo schema. DB dev hiện đã migrate sẵn. Cách chạy migration trong môi trường container để sau |

### Kết quả kiểm tra image runtime `mcr.microsoft.com/dotnet/aspnet:10.0` (2026-10-21, amd64)

- Hệ điều hành: **Ubuntu 24.04.5 LTS** (từ .NET 10, tag mặc định là Ubuntu, không còn Debian).
- Runtime: `Microsoft.AspNetCore.App 10.0.13`.
- `ASPNETCORE_HTTP_PORTS=8080`, có sẵn user `app` (UID 1654, biến `APP_UID`).
- **Không có** `curl` hay `wget`, nên không dùng được `HEALTHCHECK CMD curl ...` nếu không cài thêm.

---

## 3. Các quyết định cần chọn

### Quyết định A: Đặt Dockerfile ở đâu?

| | A1. Thư mục gốc repo | A2. `RoomSplit.Api/Dockerfile` |
|---|---|---|
| Lệnh build | `docker build -t roomsplit-api .` | `docker build -f RoomSplit.Api/Dockerfile -t roomsplit-api .` |
| Ưu điểm | Lệnh ngắn. Context và Dockerfile cùng chỗ, dễ hiểu. `.dockerignore` nằm ngay cạnh | Giống template Visual Studio. Nếu sau này có nhiều service, mỗi project có Dockerfile riêng |
| Nhược điểm | Nếu sau này có thêm service thứ hai thì phải đổi tên (`Dockerfile.api`) hoặc di chuyển | Dễ nhầm: Dockerfile nằm trong `RoomSplit.Api/` nhưng context **vẫn phải** là thư mục gốc (vì các file `.props`), và `.dockerignore` vẫn phải đặt ở gốc |

**Đề xuất: A1.** Repo chỉ có một service. Đặt ở gốc thì quan hệ giữa Dockerfile, build context và `.dockerignore` rõ ràng nhất cho việc học.

### Quyết định B: Image runtime dùng biến thể nào?

| | B1. `aspnet:10.0` (Ubuntu) | B2. `aspnet:10.0-alpine` | B3. `aspnet:10.0-noble-chiseled-extra` |
|---|---|---|---|
| Kích thước tải (nén, amd64) | ~96 MB | ~57 MB | ~71 MB |
| Có shell (`sh`) để debug | ✅ | ✅ | ❌ không có shell, không có package manager |
| tzdata (`Asia/Ho_Chi_Minh`) | ✅ đã kiểm tra | ⚠️ cần kiểm tra, có thể phải `apk add tzdata` | ✅ (biến thể `-extra` có tzdata và ICU) |
| ICU (globalization) | ✅ | ⚠️ cần kiểm tra hoặc bật invariant mode | ✅ (`-extra`) |
| Thư viện C | glibc | musl (đôi khi khác biệt hành vi) | glibc |
| Bề mặt tấn công | Trung bình | Nhỏ | **Nhỏ nhất** |
| Độ khó cho người mới | Dễ nhất | Trung bình | Khó debug (`docker exec ... sh` không chạy được) |

**Đề xuất: B1.** Đây là dự án học tập: được `docker exec -it <container> sh` vào xem filesystem, biến môi trường, file trong `/app` là rất có giá trị. Image đã có sẵn tzdata và ICU nên app chạy ngay, không cần xử lý thêm.
Khi đã quen, đổi sang B3 chỉ cần sửa **một dòng** `FROM` ở stage cuối, và đó là một bài thực hành tốt để so sánh kích thước bằng `docker images`.

### Quyết định C: Cache NuGet khi restore

| | C1. Chỉ dùng layer cache (copy `.props` và `.csproj` trước) | C2. C1 + BuildKit cache mount |
|---|---|---|
| Cách làm | `RUN dotnet restore ...` | `RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages dotnet restore ...` |
| Khi sửa code | Restore được cache hoàn toàn ✅ | Như C1 ✅ |
| Khi thêm một package | Restore lại **toàn bộ** package từ internet | Chỉ tải package **mới**, các package cũ lấy từ cache mount |
| Độ phức tạp | Thấp, đúng nội dung đang học | Thêm khái niệm cache mount, ngoài phạm vi bài |

**Đề xuất: C1.** Đủ để thấy rõ hiệu quả của thứ tự layer. Danh sách package của RoomSplit hiếm khi đổi, nên lợi ích thêm của C2 nhỏ. C2 có thể bổ sung sau như một bài nâng cao.

---

## 4. Thiết kế đề xuất (A1 + B1 + C1)

### 4.1. `Dockerfile` (dự kiến)

```dockerfile
# syntax=docker/dockerfile:1

# ---------- Stage 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Package manifests first: this layer (and the restore below) stays cached
# until a package changes. Directory.*.props are required because of
# Central Package Management.
COPY Directory.Build.props Directory.Packages.props ./
COPY RoomSplit.Api/RoomSplit.Api.csproj RoomSplit.Api/
RUN dotnet restore RoomSplit.Api/RoomSplit.Api.csproj

COPY . .
RUN dotnet publish RoomSplit.Api/RoomSplit.Api.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---------- Stage 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "RoomSplit.Api.dll"]
```

Giải thích các chỗ chưa có trong docker-basics:

| Dòng | Ý nghĩa |
|---|---|
| `# syntax=docker/dockerfile:1` | Dùng phiên bản cú pháp Dockerfile mới nhất của BuildKit |
| `/p:UseAppHost=false` | Không tạo file thực thi native `RoomSplit.Api` (chỉ cần `RoomSplit.Api.dll` vì đã chạy bằng `dotnet`) |
| `USER $APP_UID` | Chạy bằng user `app` (UID 1654) thay vì root. Nếu kẻ tấn công chiếm được app thì cũng không có quyền root trong container |
| `EXPOSE 8080` | Chỉ là **tài liệu** (metadata) cho biết app nghe cổng 8080. Không tự mở cổng ra máy host, việc đó do `-p` lúc `docker run` |
| `ENTRYPOINT [...]` dạng JSON | Dạng "exec": `dotnet` là tiến trình PID 1 và nhận được tín hiệu dừng (`docker stop`) để tắt app gọn gàng |

### 4.2. `.dockerignore` (dự kiến)

```gitignore
# Build output (any depth). In .dockerignore a bare "bin/" only matches the root.
**/bin/
**/obj/

# Secrets: must never reach the image
.env
**/appsettings.Development.json
**/secrets.json

# VCS, IDE, CI
.git/
.gitignore
.github/
.vs/
.vscode/
.idea/
**/*.user

# Not needed to build; excluding them keeps "COPY . ." cached when they change
docs/
*.md
docker-compose.yml
.env.example
**/appsettings.*.example.json
Dockerfile
.dockerignore
```

Hai điểm đáng chú ý:

1. **`**/bin/` chứ không phải `bin/`.** Khác `.gitignore`, pattern trong `.dockerignore` được tính **từ gốc context**. `bin/` chỉ loại `./bin`, còn `RoomSplit.Api/bin` vẫn bị gửi vào. Phải dùng `**/` để khớp ở mọi cấp thư mục.
2. **Loại cả `docs/`, `*.md`.** Không phải vì bí mật, mà để giữ cache: nếu không loại, chỉ sửa một file tài liệu cũng làm layer `COPY . .` thay đổi và kéo theo `dotnet publish` chạy lại.

### 4.3. Kiểm tra sau khi viết (định nghĩa "xong" của bước 1)

```bash
# 1. Build
docker build -t roomsplit-api:dev .

# 2. Build lại lần hai không đổi gì: mọi bước phải CACHED
docker build -t roomsplit-api:dev .

# 3. Sửa một file .cs rồi build lại: restore vẫn CACHED, chỉ COPY . . và publish chạy lại

# 4. Không có file bí mật trong image
docker run --rm --entrypoint sh roomsplit-api:dev -c "ls /app | grep -i -E 'appsettings|\.env'"
#    → chỉ được thấy appsettings.json

# 5. Chạy container và gọi health check "live" (không cần DB)
docker run --rm -d --name roomsplit-api -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Server=none;Database=x;User=x;Password=x;" \
  roomsplit-api:dev
curl http://localhost:8080/health/live      # → Healthy
docker exec roomsplit-api id                 # → uid=1654(app), không phải root
docker stop roomsplit-api

# 6. Quan sát layer
docker history roomsplit-api:dev
docker images roomsplit-api
```

> Bước 5 dùng connection string giả có chủ đích: `/health/live` không đụng tới DB, nên chứng minh được container chạy được mà chưa cần giải quyết chuyện mạng. `/health/ready` và các API thật sẽ lỗi cho tới bước 2–3.

---

## 5. Ảnh hưởng tới phần còn lại của repo

| File | Thay đổi |
|---|---|
| `Dockerfile` | Mới |
| `.dockerignore` | Mới |
| `README.md` | Thêm mục "Run in Docker" (lệnh build và run) |
| `CLAUDE.md` | Cập nhật Progress: Dockerfile đã xong |
| `.github/workflows/ci.yml` | Không đổi ở bước này. Có thể thêm bước `docker build` sau để CI kiểm tra Dockerfile |
| Code C# | Không đổi |

---

## 6. Tóm tắt đề xuất

| Quyết định | Đề xuất | Lý do chính |
|---|---|---|
| A. Vị trí Dockerfile | **A1**: thư mục gốc | Một service, context bắt buộc là gốc |
| B. Image runtime | **B1**: `aspnet:10.0` (Ubuntu) | Có shell để học và debug, sẵn tzdata/ICU. Đổi sang chiseled sau chỉ cần một dòng |
| C. Cache NuGet | **C1**: layer cache | Đủ cho mục tiêu học, đơn giản |

---

## 7. Kết quả triển khai

`Dockerfile` và `.dockerignore` giống thiết kế ở mục 4, với **một thay đổi**: thêm `**/appsettings.*.example.json` vào `.dockerignore`. Lần build đầu cho thấy Web SDK publish mọi file `appsettings*.json`, nên file template `appsettings.Development.example.json` đã lọt vào `/app`. File này không chứa bí mật nhưng cũng không cần trong image.

Kết quả chạy các bước kiểm tra ở mục 4.3 (Docker 29.7.2):

| # | Kiểm tra | Kết quả |
|---|---|---|
| 1 | `docker build` | ✅ Restore ~11 s, publish ~7 s, không warning |
| 2 | Build lại, không đổi gì | ✅ Mọi bước `CACHED` |
| 3 | Sửa một file `.cs` | ✅ Copy `.props`/`.csproj` và `restore` vẫn `CACHED`; chỉ `COPY . .` và `publish` chạy lại |
| 3b | Chỉ sửa file trong `docs/` | ✅ `COPY . .` và `publish` vẫn `CACHED` (nhờ `.dockerignore`) |
| 3c | Chỉ đổi thời gian sửa file (`touch`), nội dung giữ nguyên | ✅ `COPY . .` vẫn `CACHED`: cache so sánh nội dung, không so sánh mtime |
| 4 | File cấu hình trong `/app` | ✅ Chỉ có `appsettings.json` |
| 5 | `GET /health/live` | ✅ `200 Healthy` |
| 5b | `GET /health/ready` (connection string giả) | `503 Unhealthy`, đúng như dự kiến vì chưa có DB |
| 5c | `docker exec roomsplit-api id` | ✅ `uid=1654(app)`, không phải root |
| 5d | Log khởi động | `Now listening on: http://[::]:8080`, `Hosting environment: Production`, kèm warning `Failed to determine the https port for redirect` như đã dự đoán ở mục 2 |
| 6 | Kích thước image | 99.2 MB nén, 351 MB trên đĩa. Layer app chỉ chiếm **8.1 MB** |

Bước tiếp theo: cho container kết nối tới MySQL đang chạy trong `roomsplit-mysql` (Docker network).
