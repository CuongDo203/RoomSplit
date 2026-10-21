# Hot reload cho môi trường dev trong Docker: phân tích và phương án

Yêu cầu: khi phát triển, **sửa code trên máy thì container nhận thay đổi ngay**, không phải `docker compose up --build` lại image.

Liên quan: [docker-multi-env.md](docker-multi-env.md) (cấu trúc compose dev/staging/prod), [docker.md](docker.md) (Dockerfile).

---

## 1. Kiến thức nền

### 1.1. Vì sao hiện tại phải build lại?

Image dev hiện tại chứa **kết quả đã biên dịch** (`/app/*.dll`) được tạo lúc build:

```
Sửa Program.cs ──▶ docker compose up --build ──▶ restore (cache) ──▶ COPY . . ──▶ publish ~7s ──▶ tạo lại container
                   └───────────────── ~15–20 giây mỗi lần sửa ─────────────────┘
```

Container không biết gì về source trên máy bạn. Muốn thấy thay đổi, phải build image mới.

### 1.2. Hot reload cần ba mảnh ghép

| Mảnh ghép | Vai trò | Trong .NET |
|---|---|---|
| ① **Source code mới phải vào được container** | Đưa file đã sửa từ máy vào container | Bind mount, hoặc Compose Watch (mục 1.3) |
| ② **Có thứ theo dõi file và biên dịch lại** | Phát hiện file đổi, build lại, áp dụng | `dotnet watch`, cần **SDK** trong container (image `aspnet` chỉ có runtime) |
| ③ **Áp dụng thay đổi mà không mất trạng thái** | Hot reload: vá code đang chạy. Nếu không vá được thì khởi động lại app | `dotnet watch` tự làm. Với thay đổi không vá được (*rude edit*) thì cần `DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true` để tự restart mà không hỏi |

### 1.3. Hai cách đưa source vào container

#### Bind mount: "chia sẻ thư mục"

```yaml
volumes:
  - ./RoomSplit.Api:/src/RoomSplit.Api
```

Container **nhìn thẳng** vào thư mục trên máy bạn: cùng một file, hai nơi thấy. Khác với **named volume** (`mysql-data`) do Docker quản lý, bind mount trỏ tới thư mục **bạn** chọn.

#### Compose Watch: "copy khi có thay đổi"

```yaml
develop:
  watch:
    - action: sync
      path: ./RoomSplit.Api
      target: /src/RoomSplit.Api
```

Lệnh `docker compose watch` chạy **trên máy bạn**, theo dõi thư mục, và khi file đổi thì **copy** file đó vào container. Có ba loại `action`:

| Action | Khi file đổi thì |
|---|---|
| `sync` | Copy file vào container (dùng cho source `.cs`) |
| `rebuild` | Build lại image và tạo lại container (dùng cho `.csproj`, `Directory.Packages.props`: thêm package) |
| `sync+restart` | Copy file rồi restart container (dùng cho file cấu hình mà app chỉ đọc lúc khởi động) |

### 1.4. Vấn đề riêng của máy Windows

Máy này chạy **Docker Desktop trên WSL2**, còn source nằm trên ổ `D:\` (filesystem Windows). Khi bind mount một thư mục Windows vào container Linux:

| Vấn đề | Hệ quả | Cách xử lý |
|---|---|---|
| **Sự kiện thay đổi file (inotify) không truyền qua** ranh giới Windows → Linux | `dotnet watch` trong container **không biết** file đã đổi | `DOTNET_USE_POLLING_FILE_WATCHER=true`: quét file định kỳ. Tốn CPU hơn và chậm hơn một chút |
| **Đọc ghi file qua ranh giới chậm** | `dotnet build` trong container chậm hơn đáng kể | Chuyển source vào filesystem WSL (`\\wsl$\...`), nhưng thay đổi cách làm việc |
| **`bin/` và `obj/` dùng chung** giữa Windows và container | `obj/project.assets.json` chứa đường dẫn `D:\...` của Windows, container đọc vào sẽ build lỗi, và ngược lại | Che `bin/`, `obj/` bằng volume riêng trong container |

Compose Watch **không gặp** ba vấn đề này: việc theo dõi file chạy trên Windows (sự kiện file hoạt động bình thường), file được **copy** vào filesystem Linux của container (build nhanh), và có thể `ignore` `bin/`, `obj/`.

### 1.5. Dockerfile: thêm một stage `dev`

Hot reload cần SDK, mà stage `final` chỉ có runtime. Multi-stage cho phép thêm một stage dành riêng cho dev và **chọn stage khi build** bằng `target`:

```
            ┌─▶ dev      (SDK + source + dotnet watch)   ← compose dev: target: dev
build ──────┤
(restore,   └─▶ publish ──▶ final (aspnet runtime)      ← mặc định (stage cuối), staging/prod
 COPY . .)
```

BuildKit **chỉ build những stage cần cho target**. Build `final` thì bỏ qua stage `dev`, nên image production không thay đổi.

### 1.6. Nếu không chỉ định `target` thì sao?

```yaml
build:
  context: .
  target: dev     # ← bỏ dòng này đi?
```

**Quy tắc:** không có `target` thì Docker build **stage cuối cùng** trong Dockerfile. Ở RoomSplit đó là `final`, image runtime dùng cho staging/prod.

Kiểm tra thực tế (2026-10-21) trên image `roomsplit-api:dev` hiện tại, vốn được build **không có** `target`:

| Kiểm tra | Kết quả | Nghĩa là |
|---|---|---|
| `dotnet --list-sdks` | *(rỗng)* | **Không có SDK**, nên không biên dịch được |
| `dotnet watch --version` | `The application 'watch' does not exist` | **Không có `dotnet watch`** |
| `ls /src` | `No such file or directory` | Không có source code trong image |
| `id -u` / `touch /src-test` | `1654` / `Permission denied` | Container chạy bằng user `app`, không tạo được thư mục `/src` |

Vậy nếu bỏ `target: dev`:

1. Compose build stage `final`: image chỉ có runtime và các file `.dll` đã biên dịch sẵn.
2. Container chạy `dotnet RoomSplit.Api.dll` (ENTRYPOINT của `final`), **không phải** `dotnet watch`.
3. Khi bạn sửa file, Compose Watch cố copy vào `/src/RoomSplit.Api`, nhưng thư mục không tồn tại và user `app` không có quyền tạo, nên sync lỗi. Kể cả copy được thì cũng **không có gì biên dịch lại**.
4. Kết quả: API vẫn chạy bình thường nhưng **luôn là code cũ**. Đây là lỗi "im lặng", dễ làm bạn tưởng hot reload không hoạt động vì lý do khác.

> **Ngược lại cũng nguy hiểm:** staging/prod build bằng `docker build .` (không có `--target`). Nếu một ngày ai đó **đặt stage `dev` ở cuối** Dockerfile, image production sẽ là image SDK (to gấp mấy lần, có compiler, chạy bằng root, chạy `dotnet watch`) mà **không có lỗi nào báo**. Vì vậy:
> - luôn giữ `final` là **stage cuối cùng**, có comment ghi rõ điều này trong Dockerfile;
> - hoặc chỉ định rõ khi build production: `docker build --target final ...`.

| Cách build | Stage được build |
|---|---|
| `docker build .` hoặc compose không có `target` | Stage **cuối cùng** (`final`) |
| `docker build --target dev .` hoặc compose `target: dev` | `build` → `dev` (bỏ qua `publish`, `final`) |
| `docker build --target final .` | `build` → `publish` → `final` (bỏ qua `dev`) |

---

## 2. Hiện trạng ảnh hưởng tới thiết kế

| Hiện trạng | Hệ quả |
|---|---|
| Docker Compose **v5.4.0** | Hỗ trợ `develop.watch` (có từ v2.22) |
| Docker Desktop trên WSL2, source ở `D:\` | Bind mount gặp các vấn đề ở mục 1.4 |
| `launchSettings.json` có `applicationUrl: http://localhost:5222` | `dotnet run` mặc định dùng launch profile, sẽ nghe `localhost:5222` **bên trong** container và không truy cập được từ ngoài. Phải chạy với `--no-launch-profile` để dùng `ASPNETCORE_HTTP_PORTS=8080` của image |
| `.dockerignore` loại `appsettings.Development.json` và `bin/`, `obj/` | Compose Watch tôn trọng `.dockerignore`, nên file password không bị sync vào. Cần kiểm tra khi triển khai |
| `docker-compose.override.yml` là file dev | Thay đổi chỉ nằm trong file này và trong `Dockerfile`. Staging/prod không bị ảnh hưởng |
| Connection string truyền qua biến môi trường | Không đổi, hot reload không ảnh hưởng tới cấu hình |

---

## 3. Các phương án

### O1. Bind mount + `dotnet watch` (polling)

```yaml
api:
  build: { context: ., target: dev }
  environment:
    DOTNET_USE_POLLING_FILE_WATCHER: "true"
  volumes:
    - ./RoomSplit.Api:/src/RoomSplit.Api      # source từ máy
    - api-bin:/src/RoomSplit.Api/bin          # che bin/obj của Windows
    - api-obj:/src/RoomSplit.Api/obj
```

| Ưu điểm | Nhược điểm |
|---|---|
| Cách phổ biến nhất trên mạng, chạy được với mọi phiên bản compose | Trên Windows phải dùng **polling**: tốn CPU, độ trễ phát hiện ~1–4 giây |
| Chạy bằng lệnh `docker compose up` bình thường | Build qua ranh giới Windows → WSL2 **chậm** |
| | Phải che `bin/`/`obj/` bằng volume, dễ gây lỗi khó hiểu nếu quên. `obj/` trong volume cũ không tự cập nhật khi đổi package |
| | Container **ghi được** vào thư mục source trên máy bạn |

### O2. Compose Watch + `dotnet watch` (đề xuất)

```yaml
api:
  build: { context: ., target: dev }
  develop:
    watch:
      - action: sync
        path: ./RoomSplit.Api
        target: /src/RoomSplit.Api
        ignore: [bin/, obj/]
      - action: rebuild
        path: ./RoomSplit.Api/RoomSplit.Api.csproj
      - action: rebuild
        path: ./Directory.Packages.props
```

Chạy bằng `docker compose watch` (hoặc `docker compose up --watch`).

| Ưu điểm | Nhược điểm |
|---|---|
| **Không cần polling**: việc theo dõi file chạy trên Windows | Phải nhớ dùng `docker compose watch` thay vì `up` |
| Build nhanh vì source nằm trong filesystem Linux của container | Khái niệm mới (`develop.watch`), ít tài liệu tiếng Việt |
| Không đụng `bin/`/`obj/` của Windows | Đồng bộ **một chiều** máy → container. File tạo trong container (ví dụ `dotnet ef migrations add` chạy trong container) không về máy. Vẫn chạy `dotnet ef` trên máy như hiện nay nên không ảnh hưởng |
| Sửa `.csproj` hoặc thêm package thì **tự rebuild** image | |
| Container không ghi được vào source trên máy | |

### O3. Không container hóa API khi dev: `dotnet watch` chạy trên máy, chỉ MySQL trong Docker

```powershell
docker compose up -d mysql
cd RoomSplit.Api; dotnet watch
```

| Ưu điểm | Nhược điểm |
|---|---|
| Nhanh nhất, **debug bằng breakpoint** trong IDE dễ nhất | **Không đáp ứng yêu cầu** "container nhận thay đổi" |
| Không cần cấu hình gì thêm, đã chạy được ngay hôm nay | Môi trường chạy là Windows, khác Linux của staging/prod (tên file phân biệt hoa thường, múi giờ…) |

### So sánh nhanh

| Tiêu chí | O1 bind mount | O2 Compose Watch | O3 chạy trên host |
|---|---|---|---|
| Đáp ứng "container nhận thay đổi" | ✅ | ✅ | ❌ |
| Phát hiện thay đổi trên Windows | Polling (chậm, tốn CPU) | Sự kiện file thật | Sự kiện file thật |
| Tốc độ build lại | Chậm (qua ranh giới) | Nhanh | Nhanh nhất |
| Thêm package | Phải xử lý `obj/` thủ công | Tự rebuild | Tự xử lý |
| Độ phức tạp cấu hình | Trung bình (volume che bin/obj) | Trung bình | Không có |
| Debug breakpoint | Phải attach vào container | Phải attach vào container | Dễ nhất |

---

## 4. Đề xuất: O2 (Compose Watch)

- Đáp ứng đúng yêu cầu, và **phù hợp nhất với máy Windows + WSL2** đang dùng: không polling, build nhanh, không xung đột `bin/`/`obj/`.
- Thêm package thì tự rebuild, không có trường hợp "quên xóa volume `obj`" như O1.
- Thay đổi chỉ nằm ở `Dockerfile` (thêm stage `dev`) và `docker-compose.override.yml`. **Image staging/production không thay đổi.**
- **O3 vẫn dùng được song song** khi cần debug bằng breakpoint: `docker compose up -d mysql` rồi `dotnet watch` trên máy, như hiện nay.

### 4.1. Thiết kế chi tiết

**`Dockerfile`**: tách stage `build` thành `build` (restore + copy source) → `dev` → `publish` → `final`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY RoomSplit.Api/RoomSplit.Api.csproj RoomSplit.Api/
RUN dotnet restore RoomSplit.Api/RoomSplit.Api.csproj
COPY . .

# ---------- dev: hot reload (docker compose watch) ----------
FROM build AS dev
ENV DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true
ENTRYPOINT ["dotnet", "watch", "--project", "RoomSplit.Api", "run", "--no-launch-profile"]

# ---------- publish ----------
FROM build AS publish
RUN dotnet publish RoomSplit.Api/RoomSplit.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---------- final: default target, keep this stage LAST ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS final
...  # không đổi, chỉ đổi --from=build thành --from=publish
```

**`docker-compose.override.yml`** (service `api`):

```yaml
  api:
    build:
      context: .
      target: dev
    container_name: roomsplit-api
    environment: ...   # không đổi
    ports: ...         # không đổi
    develop:
      watch:
        - action: sync
          path: ./RoomSplit.Api
          target: /src/RoomSplit.Api
          ignore:
            - bin/
            - obj/
        - action: rebuild
          path: ./RoomSplit.Api/RoomSplit.Api.csproj
        - action: rebuild
          path: ./Directory.Packages.props
        - action: rebuild
          path: ./Directory.Build.props
```

**Lệnh dùng hằng ngày:**

```powershell
docker compose watch          # build (lần đầu), chạy, rồi theo dõi thay đổi. Ctrl+C để dừng theo dõi
docker compose logs -f api    # (terminal khác) xem dotnet watch báo hot reload
```

### 4.2. Kiểm tra (định nghĩa "xong")

| # | Kiểm tra | Kỳ vọng |
|---|---|---|
| 1 | `docker compose watch` | API chạy trên 8080, log có `dotnet watch` |
| 2 | Sửa thân một method (ví dụ chuỗi trả về) | Log `Hot reload succeeded` (hoặc tương đương), request sau thấy thay đổi, **không** build lại image |
| 3 | Thêm action mới hoặc đổi chữ ký method (rude edit) | App tự restart, endpoint mới gọi được |
| 4 | Sửa `.csproj` | Compose build lại image và tạo lại container |
| 5 | `appsettings.Development.json` (có password) | **Không** bị sync vào container |
| 6 | Image staging/prod (`docker build .`, target mặc định) | Vẫn là image `aspnet` runtime, không chứa SDK |
| 7 | `docker compose up -d` (không watch) | Vẫn chạy được (dùng image `dev`, chỉ là không theo dõi thay đổi) |

---

## 5. Ảnh hưởng tới phần còn lại của repo

| File | Thay đổi |
|---|---|
| `Dockerfile` | Thêm stage `dev`, tách `publish` thành stage riêng |
| `docker-compose.override.yml` | `build.target: dev`, thêm `develop.watch` |
| `README.md` | Thêm cách chạy dev với hot reload |
| `docs/docker.md` | Ghi chú Dockerfile đã có thêm stage |
| `CLAUDE.md` | Cập nhật Progress |
| `docker-compose.yml`, `docker-compose.prod.yml`, code C# | Không đổi |

👉 Chọn O1, O2 hoặc O3 (hoặc đồng ý đề xuất O2) để bắt đầu triển khai.
