# Docker cơ bản, áp dụng vào RoomSplit

Tài liệu học tập: các khái niệm nền tảng cần nắm trước khi build image cho `RoomSplit.Api` và chạy nó trong container.

## Mục lục
1. [Bốn khái niệm chính: image, container, layer, registry](#1-bốn-khái-niệm-chính)
   - [1.3. Layer: kiến trúc nhiều lớp](#13-layer-kiến-trúc-nhiều-lớp) (union filesystem, copy-on-write, build cache)
2. [Dockerfile hai stage (multi-stage build)](#2-dockerfile-hai-stage-multi-stage-build)
3. [Copy `.csproj` và restore trước, copy source sau](#3-copy-csproj-và-restore-trước-copy-source-sau)
4. [`.dockerignore`](#4-dockerignore)
5. [Tóm tắt nhanh](#5-tóm-tắt-nhanh)
6. [Bước tiếp theo](#6-bước-tiếp-theo)

Tìm hiểu sâu về volume và network: [docker-volumes-networks.md](docker-volumes-networks.md).

---

## 1. Bốn khái niệm chính

### 1.1. Image

Image là một **bản đóng gói chỉ đọc**, chứa mọi thứ cần để chạy app:

- hệ điều hành tối thiểu (ví dụ Ubuntu, Debian hoặc Alpine),
- .NET runtime,
- các file `.dll` của app,
- lệnh khởi động.

> **Ví von:** image giống một **class**. Nó là bản thiết kế, tự nó không chạy.

Tên image có dạng `repository:tag`, ví dụ:

| Image | Ý nghĩa |
|---|---|
| `mcr.microsoft.com/dotnet/aspnet:10.0` | Image ASP.NET Core 10 runtime của Microsoft |
| `mysql:8.0` | Image MySQL 8.0 trên Docker Hub (đang dùng trong `docker-compose.yml`) |
| `roomsplit-api:dev` | Image của chúng ta, tự build |

### 1.2. Container

Container là **một phiên bản đang chạy của image**.

> **Ví von:** nếu image là class thì container là **object** được tạo từ class đó.

- Từ một image có thể chạy nhiều container. Mỗi container có tiến trình, mạng và filesystem riêng.
- Container có một **lớp ghi mỏng** nằm trên image. Khi xóa container thì mọi thứ ghi trong lớp đó cũng mất.
  - Vì vậy MySQL trong `docker-compose.yml` phải dùng **volume** `mysql-data` để giữ dữ liệu khi container bị xóa hoặc tạo lại.
- Container **không phải máy ảo**. Nó dùng chung kernel với máy host nên khởi động gần như tức thì và rất nhẹ.

Các lệnh hay dùng:

```bash
docker run mysql:8.0    # tạo và chạy container từ image
docker ps               # xem các container đang chạy (thêm -a để xem cả container đã dừng)
docker images           # xem các image có trên máy
docker logs <tên>       # xem log của container
docker rm <tên>         # xóa container
```

### 1.3. Layer: kiến trúc nhiều lớp

#### a) Layer là gì?

Một **layer** là một **tập các thay đổi trên filesystem**: file được thêm, sửa hoặc xóa so với layer bên dưới. Layer giống **một commit trong git**: nó không chứa toàn bộ thư mục, chỉ chứa *phần khác biệt* (diff).

Image = **danh sách các layer xếp chồng** + **metadata** (lệnh khởi động, biến môi trường, cổng, user…).

```
          Image roomsplit-api:dev (số đo thật từ docker history)
┌──────────────────────────────────────────────────────┐
│ L4  COPY --from=build /app/publish          8.1 MB   │  ← của chúng ta, đổi mỗi lần sửa code
│     WORKDIR /app                            8 kB     │
├──────────────────────────────────────────────────────┤
│ L3  ASP.NET Core runtime                    27.4 MB  │  ┐
├──────────────────────────────────────────────────────┤  │
│ L2  .NET runtime                            83.2 MB  │  │ lấy nguyên từ image aspnet:10.0
├──────────────────────────────────────────────────────┤  │ (FROM kéo về tất cả các layer này)
│ L1  apt: ICU, tzdata, OpenSSL… + user app   45.9 MB  │  │
│     Ubuntu 24.04 base                       87.6 MB  │  ┘
└──────────────────────────────────────────────────────┘
  + metadata: ENTRYPOINT, ENV, USER, EXPOSE… (0 B)
```

> Mỗi ô L1–L4 ở trên thực ra gồm một hoặc vài layer. Tổng cộng app của chúng ta chỉ thêm ~8 MB trên nền ~245 MB của image `aspnet:10.0`. Tự xem bằng `docker history roomsplit-api:dev` (mục h).

#### b) Lệnh nào tạo layer?

| Loại lệnh | Lệnh | Tạo gì |
|---|---|---|
| Thay đổi filesystem | `RUN`, `COPY`, `ADD` | **Layer mới** chứa phần file thay đổi |
| Lấy image nền | `FROM` | Không tạo layer mới, mà **kế thừa toàn bộ layer** của image nền |
| Tạo thư mục làm việc | `WORKDIR` | Layer rất nhỏ (vài kB) nếu thư mục chưa tồn tại |
| Chỉ là cấu hình | `ENV`, `USER`, `EXPOSE`, `ENTRYPOINT`, `CMD`, `LABEL` | Chỉ ghi **metadata**, layer rỗng (0 B) |

#### c) Gộp các layer thành một filesystem (union filesystem)

Khi container chạy, nó **không** thấy từng layer riêng lẻ. Docker dùng **union filesystem** (trên Linux là `overlay2`) để **chồng các layer lên nhau** thành một cây thư mục duy nhất:

- Nhìn từ trên xuống, file ở layer **trên** che file cùng đường dẫn ở layer **dưới**.
- Mọi layer của image đều **chỉ đọc** (read-only).
- Mỗi container có thêm **một layer ghi được** (writable / container layer) nằm trên cùng.

```
   Container A              Container B
┌───────────────┐        ┌───────────────┐
│ writable layer│        │ writable layer│   ← riêng mỗi container, mất khi xóa container
└───────┬───────┘        └───────┬───────┘
        └──────────┬─────────────┘
        ┌──────────▼──────────┐
        │ L4  app             │
        │ L3  ASP.NET runtime │   ← chỉ đọc, DÙNG CHUNG giữa các container
        │ L2  .NET runtime    │
        │ L1  Ubuntu base     │
        └─────────────────────┘
```

Vì thế chạy 10 container từ cùng một image **không** tốn gấp 10 lần dung lượng: phần image chỉ lưu một lần, mỗi container chỉ thêm phần nó ghi.

#### d) Copy-on-write (CoW)

Khi container **sửa** một file nằm trong layer chỉ đọc:

1. Docker **copy file đó lên** writable layer (*copy-up*),
2. rồi sửa trên bản copy. Layer gốc **không bao giờ thay đổi**.

Khi container **xóa** một file của image, Docker ghi một dấu **"whiteout"** vào writable layer để che file đó đi. File gốc vẫn nằm nguyên trong layer bên dưới.

**Hệ quả thực tế:**
- Dữ liệu ghi nhiều (database, log, upload) **không nên** ghi vào writable layer: chậm (do copy-up) và mất khi xóa container. Hãy dùng **volume**, như `mysql-data` trong `docker-compose.yml`.

#### e) Xóa ở layer sau KHÔNG làm image nhỏ đi

Cơ chế whiteout cũng áp dụng khi build. Xét ví dụ:

```dockerfile
COPY .env /src/.env        # L5: chứa password
RUN rm /src/.env           # L6: chỉ thêm "whiteout", L5 vẫn còn nguyên
```

- Image **không nhỏ đi**, vì L5 vẫn được lưu và vẫn được push lên registry.
- **Bí mật vẫn lấy ra được**: ai có image đều có thể giải nén L5 và đọc `.env`.

Đây là lý do:
- bí mật phải bị chặn **ngay từ đầu** bằng `.dockerignore` ([mục 4](#4-dockerignore)),
- multi-stage build ([mục 2](#2-dockerfile-hai-stage-multi-stage-build)) hữu ích: các layer của stage `build` (source code, SDK, `obj/`) **không hề có mặt** trong image cuối.
- nếu cần tạo rồi xóa file tạm, làm trong **cùng một lệnh `RUN`**: `RUN tải-file && dùng-file && rm file`.

#### f) Layer được định danh bằng nội dung, và vì thế được chia sẻ

Mỗi layer có một mã **digest SHA-256** tính từ **nội dung** của nó (*content-addressable*), ví dụ `sha256:3c1d…`.

- Hai image có cùng layer (cùng digest) thì layer đó chỉ **lưu một lần** trên đĩa.
- Khi `docker pull`, Docker chỉ tải những layer **chưa có** trên máy (`Already exists`).
- Khi `docker push`, chỉ đẩy những layer registry **chưa có** (`Layer already exists`).

Ví dụ: sau này có thêm image thứ hai cũng dựa trên `aspnet:10.0`, nó dùng lại L1–L3 có sẵn và chỉ tốn thêm vài MB cho layer app.

#### g) Build cache: Docker quyết định dùng lại layer thế nào?

Khi `docker build`, Docker đi **từ trên xuống**, mỗi lệnh kiểm tra xem đã có layer cache phù hợp chưa:

| Lệnh | Cache được dùng lại khi |
|---|---|
| `FROM` | Image nền vẫn là bản đã có trên máy |
| `COPY` / `ADD` | Chuỗi lệnh giống hệt **và** **nội dung** (checksum) các file được copy không đổi. Thời gian sửa file (mtime) không ảnh hưởng |
| `RUN` | Chỉ cần **chuỗi lệnh** giống hệt. Docker **không** biết bên ngoài đã thay đổi gì (ví dụ package mới trên internet) |
| Các lệnh khác | Chuỗi lệnh giống hệt |

**Quy tắc quan trọng nhất:**

> Khi một lệnh **không** dùng được cache (*cache miss*), **mọi lệnh phía sau** cũng phải chạy lại, kể cả khi bản thân chúng không đổi.

Vì vậy **thứ tự lệnh** quyết định tốc độ build:

```
Ít thay đổi  ─┐   FROM sdk:10.0
              │   COPY *.props, *.csproj      ← chỉ đổi khi thêm/sửa package
              │   RUN dotnet restore          ← chậm, nên muốn luôn được cache
              │   COPY . .                    ← đổi mỗi lần sửa code
Hay thay đổi ─┘   RUN dotnet publish
```

Đặt thứ **ít thay đổi lên trên, hay thay đổi xuống dưới**. Đây chính là mẹo ở [mục 3](#3-copy-csproj-và-restore-trước-copy-source-sau).

Khi build, bạn sẽ thấy chữ `CACHED` ở những bước dùng lại cache:

```
 => CACHED [build 3/6] COPY Directory.Build.props Directory.Packages.props ./
 => CACHED [build 4/6] COPY RoomSplit.Api/RoomSplit.Api.csproj RoomSplit.Api/
 => CACHED [build 5/6] RUN dotnet restore ...
 => [build 6/6] COPY . .                       ← cache miss từ đây do code đã đổi
```

Muốn bỏ qua cache hoàn toàn: `docker build --no-cache ...`

#### h) Các lệnh để quan sát layer

```bash
docker history roomsplit-api:dev          # từng layer: lệnh tạo ra nó và kích thước
docker image inspect roomsplit-api:dev \
  --format '{{json .RootFS.Layers}}'      # danh sách digest của các layer
docker system df                          # dung lượng image, container, volume, build cache
docker builder prune                      # dọn build cache khi đầy đĩa
```

Khi build xong image RoomSplit, hãy chạy thử `docker history` để đối chiếu với sơ đồ ở mục a.

### 1.4. Registry

Registry là **kho lưu image**.

> **Ví von:** registry đóng vai trò giống **NuGet**, nhưng dành cho image.

| Registry | Ví dụ |
|---|---|
| Docker Hub | `mysql:8.0` |
| Microsoft Container Registry (`mcr.microsoft.com`) | các image .NET (`sdk`, `aspnet`, `runtime`) |
| GitHub Container Registry (`ghcr.io`) | thường dùng cùng GitHub Actions |
| AWS ECR, Azure ACR, Google Artifact Registry | registry của các cloud |

- `docker pull` tải image về, `docker push` đẩy image lên.
- Image bạn build trên máy **chỉ nằm ở máy local** cho đến khi bạn push.

### 1.5. Luồng tổng thể

```
Dockerfile ──docker build──▶ Image ──docker run──▶ Container
                               │ ▲
                     docker push│ │docker pull
                               ▼ │
                            Registry
```

---

## 2. Dockerfile hai stage (multi-stage build)

### Vấn đề

| Mục đích | Cần gì | Image | Kích thước (xấp xỉ) |
|---|---|---|---|
| **Build** app | SDK: compiler, NuGet, MSBuild | `mcr.microsoft.com/dotnet/sdk:10.0` | ~351 MB khi tải (đã nén) |
| **Chạy** app | Chỉ ASP.NET Core runtime | `mcr.microsoft.com/dotnet/aspnet:10.0` | ~96 MB khi tải (đã nén), ~340 MB trên đĩa |

> Số liệu đo thực tế ngày 2026-10-21 với tag `10.0` (amd64). Từ .NET 10, tag mặc định của image .NET dùng **Ubuntu 24.04**, không còn là Debian.

Nếu dùng image SDK để chạy production thì:

- image **nặng**, push/pull và deploy chậm,
- chứa nhiều công cụ thừa, làm **tăng bề mặt tấn công** (càng nhiều phần mềm thì càng nhiều lỗ hổng tiềm ẩn).

### Giải pháp: hai stage trong cùng một Dockerfile

```dockerfile
# ---------- Stage 1: build (to, có SDK) ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# ... copy source, restore ...
RUN dotnet publish -c Release -o /app/publish

# ---------- Stage 2: runtime (nhỏ, chỉ có runtime) ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RoomSplit.Api.dll"]
```

Các điểm cần hiểu:

- **Image cuối cùng chỉ gồm stage cuối.** Toàn bộ stage `build` (SDK, source code, thư mục `obj/`) bị bỏ đi. Chỉ phần output publish được copy sang.
- `AS build` **đặt tên** cho stage, sau đó `COPY --from=build` lấy file từ stage đó.
- `WORKDIR` đặt thư mục làm việc (tự tạo nếu chưa có), tương tự lệnh `cd`.
- `ENTRYPOINT` là lệnh chạy khi container khởi động.

### Lưu ý riêng của image .NET 8 trở lên

| Đặc điểm | Chi tiết |
|---|---|
| Cổng mặc định | App lắng nghe **8080**, không phải 80 (biến `ASPNETCORE_HTTP_PORTS=8080`) |
| User không phải root | Image có sẵn user `app`. Nên thêm `USER app` để app không chạy bằng quyền root |
| Môi trường | Mặc định là `Production`, nên Swagger UI (chỉ bật trong Development) sẽ **không** hiện, trừ khi đặt `ASPNETCORE_ENVIRONMENT=Development` |

---

## 3. Copy `.csproj` và restore trước, copy source sau

Đây là cách áp dụng **cache layer** ở mục 1.3.

### ❌ Cách chưa tốt

```dockerfile
COPY . .                 # chỉ cần sửa 1 file .cs là layer này đổi
RUN dotnet restore       # → restore lại toàn bộ NuGet mỗi lần (chậm)
RUN dotnet publish ...
```

Mỗi lần sửa một dòng code, layer `COPY . .` thay đổi, nên mọi lệnh phía sau phải chạy lại, kể cả `dotnet restore` vốn mất nhiều thời gian.

### ✅ Cách tốt

```dockerfile
# 1) Chỉ copy các file khai báo package
COPY Directory.Build.props Directory.Packages.props ./
COPY RoomSplit.Api/RoomSplit.Api.csproj RoomSplit.Api/

# 2) Restore: layer này CHỈ chạy lại khi các file ở bước 1 thay đổi
RUN dotnet restore RoomSplit.Api/RoomSplit.Api.csproj

# 3) Bây giờ mới copy toàn bộ source: sửa code chỉ làm đổi từ đây trở xuống
COPY . .
RUN dotnet publish RoomSplit.Api/RoomSplit.Api.csproj -c Release --no-restore -o /app/publish
```

### Vì sao hiệu quả?

- Bạn **sửa code thường xuyên**, còn **danh sách package thì hiếm khi đổi**.
- Tách restore ra trước giúp layer restore gần như luôn được lấy từ cache, nên build lại chỉ mất vài giây thay vì cả phút.
- `--no-restore` ở bước publish giúp không restore lại lần hai.

### ⚠️ Điểm riêng của RoomSplit: Central Package Management

Dự án dùng **Central Package Management (CPM)**: version package nằm trong `Directory.Packages.props`, còn csproj **không có `Version`**:

```xml
<!-- RoomSplit.Api.csproj -->
<PackageReference Include="Pomelo.EntityFrameworkCore.MySql" />   <!-- không có Version -->
```

Do đó, ở bước restore phải copy thêm:

| File | Lý do |
|---|---|
| `Directory.Packages.props` | Chứa version của mọi package. Thiếu file này thì restore lỗi vì không biết version |
| `Directory.Build.props` | Chứa `TargetFramework` (`net10.0`), `Nullable`, `ImplicitUsings`… Thiếu file này thì project không biết target framework |

---

## 4. `.dockerignore`

Khi chạy `docker build .`, Docker gửi **toàn bộ thư mục** (gọi là *build context*) cho Docker engine **trước khi** build. `.dockerignore` loại bớt những thứ không cần gửi, cú pháp tương tự `.gitignore`.

### Những gì cần loại trừ

| Loại trừ | Lý do |
|---|---|
| `bin/`, `obj/` | Output build trên Windows. Nếu bị copy vào, chúng có thể **đè lên kết quả restore** trong container (file `obj/project.assets.json` chứa đường dẫn Windows `D:\...`) và gây lỗi build khó hiểu. Chúng cũng làm context nặng hơn. |
| `.git` | Lịch sử git không cần cho build, lại nặng. Nếu không loại, mỗi commit còn làm thay đổi layer `COPY . .` và **phá cache**. |
| `.env` | Chứa `MYSQL_ROOT_PASSWORD`. **Bí mật không được nằm trong image**, vì ai pull image về cũng đọc được. |
| `appsettings.Development.json` | Riêng repo này: file này chứa **password DB thật**. Phải loại trừ giống như `.env`. |
| `.vs/`, `*.user` | File của IDE, không liên quan đến build. |

### 🔐 Quy tắc chung về bí mật

> Config bí mật được truyền vào **lúc chạy container**, không bao giờ build sẵn vào image.

```bash
docker run -e ConnectionStrings__DefaultConnection="Server=...;Password=..." roomsplit-api:dev
```

Dấu `__` (hai gạch dưới) trong tên biến môi trường tương ứng với dấu `:` trong cấu hình ASP.NET Core. Vì vậy `ConnectionStrings__DefaultConnection` sẽ được đọc như `ConnectionStrings:DefaultConnection` trong `appsettings.json`.

---

## 5. Tóm tắt nhanh

| Khái niệm | Một câu |
|---|---|
| **Image** | Bản đóng gói chỉ đọc của app và môi trường chạy (giống class) |
| **Container** | Image đang chạy, có lớp ghi riêng, mất khi xóa (giống object) |
| **Layer** | Phần diff filesystem do `RUN`/`COPY`/`ADD` tạo ra, chỉ đọc, định danh bằng SHA-256 và dùng chung giữa các image |
| **Union FS + CoW** | Các layer được chồng thành một filesystem. Container ghi vào layer riêng trên cùng, sửa file của image thì copy-up trước |
| **Build cache** | Cache miss ở một lệnh thì mọi lệnh sau chạy lại, nên đặt thứ ít đổi lên trên |
| **Xóa không giảm size** | `RUN rm` ở layer sau chỉ che file, không xóa khỏi image. Bí mật phải chặn bằng `.dockerignore` |
| **Registry** | Kho lưu và phân phối image (giống NuGet) |
| **Multi-stage** | Build bằng `sdk`, chạy bằng `aspnet`. Image cuối nhỏ và an toàn hơn |
| **Restore trước** | Copy `*.props` và `.csproj`, restore, rồi mới copy source, để tận dụng cache |
| **`.dockerignore`** | Loại `bin/`, `obj/`, `.git`, `.env` và các file chứa bí mật khỏi build context |

---

## 6. Bước tiếp theo

1. Viết `Dockerfile` và `.dockerignore` cho `RoomSplit.Api`.
2. `docker build -t roomsplit-api:dev .`
3. `docker run` và kết nối tới MySQL.
   Sẽ gặp một vấn đề: **bên trong container, `localhost` là chính container đó**, không phải máy host, nên API không kết nối được MySQL theo connection string hiện tại. Đây là lúc tìm hiểu **Docker network**.

Theo quy trình trong `CLAUDE.md`, trước khi viết code sẽ có tài liệu phân tích các phương án (ví dụ: chỉ dùng `docker run` hay thêm service `api` vào `docker-compose.yml`) để chọn.
