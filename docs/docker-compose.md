# Chạy API + MySQL bằng docker-compose: phân tích và phương án

Phạm vi: **bước 2**, thêm service `api` vào docker-compose để `docker compose up` chạy cả API (image từ [Dockerfile](../Dockerfile)) lẫn MySQL, và API kết nối được tới MySQL.

Liên quan: [docker-basics.md](docker-basics.md) (khái niệm), [docker.md](docker.md) (Dockerfile, bước 1).

> **Trạng thái:** đã chọn **A1 + B1 + C1 + D1 + E1** (2026-10-21) và đã triển khai. Kết quả kiểm tra ở [mục 7](#7-kết-quả-triển-khai).

> 🔀 **Cập nhật 2026-10-21:** file compose đã được tách thành `docker-compose.yml` (chung) + `docker-compose.override.yml` (dev) + `docker-compose.prod.yml` (staging/prod). Xem [docker-multi-env.md](docker-multi-env.md). Phần giải thích từng dòng ở mục 8 vẫn đúng: các dòng chỉ được chia sang các file khác nhau. Dev có thêm `ports`, `container_name`, `build`, `environment` trong file override.

> 📖 **Mới làm quen với compose?** Đọc [mục 8: Đọc hiểu file `docker-compose.yml`](#8-đọc-hiểu-file-docker-composeyml) trước. Mục này giải thích từng dòng của file đang dùng. Cơ chế đọc file `.env` nằm ở [mục 9](#9-cơ-chế-compose-đọc-file-env).

---

## 1. Kiến thức mới cần nắm

### 1.1. docker-compose là gì?

`docker run` chỉ chạy **một** container, và mọi tham số (`-p`, `-e`, `--network`, `-v`…) phải gõ trên dòng lệnh. Khi app gồm nhiều container, docker-compose cho phép **mô tả toàn bộ hệ thống trong một file YAML** rồi bật hoặc tắt bằng một lệnh:

| `docker run` (thủ công) | `docker-compose.yml` (khai báo) |
|---|---|
| `docker run -d --name roomsplit-mysql -p 3307:3306 -e ... -v ... mysql:8.0` | `services: mysql: image: mysql:8.0, ports: ..., environment: ...` |
| Phải tự tạo network, tự nhớ thứ tự khởi động | Compose tự tạo network, tự sắp thứ tự theo `depends_on` |
| Mỗi container một lệnh | `docker compose up -d` cho tất cả |

Một số khái niệm của compose:
- **Service**: một thành phần của hệ thống (`mysql`, `api`). Mỗi service chạy một hoặc nhiều container.
- **Project**: tên nhóm, mặc định là tên thư mục (`roomsplit`). Compose dùng tên này làm tiền tố khi đặt tên network và volume: `roomsplit_default`, `roomsplit_mysql-data`.

### 1.2. Docker network: vì sao `localhost` không dùng được

```
┌─────────────── Máy Windows của bạn ────────────────────────────┐
│                                                                │
│  localhost:3307 ──┐        localhost:8080 ──┐                  │
│                   │ (port mapping)          │ (port mapping)   │
│  ┌──────── network "roomsplit_default" ─────┼───────────────┐  │
│  │                ▼                         ▼               │  │
│  │   ┌─────────────────────┐    ┌─────────────────────┐     │  │
│  │   │ container "mysql"   │◀───│ container "api"     │     │  │
│  │   │ lắng nghe :3306     │    │ Server=mysql;       │     │  │
│  │   └─────────────────────┘    │ Port=3306           │     │  │
│  │                              └─────────────────────┘     │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────┘
```

- Mỗi container có **network stack riêng**. `localhost` bên trong container `api` là **chính container `api`**, mà trong đó không có MySQL.
- Compose tự tạo network `roomsplit_default` và cho mọi service tham gia. Trong network này, Docker có **DNS nội bộ**: **tên service chính là hostname**. Container `api` gọi `mysql` thì được phân giải thành IP của container MySQL.
- **Hai container nói chuyện trực tiếp qua cổng bên trong (3306)**, không đi qua cổng 3307 của máy host. Cổng 3307 chỉ dành cho **máy bạn** (công cụ như DBeaver, `dotnet ef`, `dotnet run`).

| Ai kết nối | Connection string |
|---|---|
| `dotnet run` trên Windows | `Server=localhost;Port=3307;...` (qua port mapping) |
| Container `api` trong compose | `Server=mysql;Port=3306;...` (qua network nội bộ) |

> Cách `host.docker.internal` ở câu trả lời trước thì đi vòng: container → máy host → port mapping 3307 → container MySQL. Cách này chạy được nhưng phụ thuộc Docker Desktop, và chậm, rối hơn mạng nội bộ.

### 1.3. Thứ tự khởi động và healthcheck

MySQL cần vài giây (lần đầu tạo volume thì hơn 10 giây) mới nhận kết nối. Có hai mức chờ:

| Khai báo | Compose chờ tới khi |
|---|---|
| `depends_on: [mysql]` | Container MySQL **đã khởi động**, có thể MySQL chưa sẵn sàng nhận kết nối |
| `depends_on: mysql: condition: service_healthy` | **Healthcheck** của MySQL báo `healthy` (service `mysql` hiện đã có healthcheck `mysqladmin ping`) |

---

## 2. Hiện trạng

| Hiện trạng (kiểm tra 2026-10-21) | Hệ quả |
|---|---|
| `docker-compose.yml` có một service `mysql`, `container_name: roomsplit-mysql`, có healthcheck | Thêm service `api`, không sửa `mysql`. Compose **có thể tạo lại** container MySQL khi `up` (xem mục 7), nhưng dữ liệu nằm trong volume nên không mất |
| Volume `roomsplit_mysql-data` đã có dữ liệu, đã áp dụng đủ 2 migration (`InitialCreate`, `AddRoomIdToSettlements`) | API chạy được ngay với DB hiện tại |
| MySQL chỉ có user `root` | Biến `MYSQL_USER`/`MYSQL_PASSWORD` của image MySQL **chỉ có tác dụng khi volume rỗng** (lần khởi tạo đầu). Với volume hiện tại, thêm biến này sẽ không tạo user |
| `.env` có `MYSQL_ROOT_PASSWORD`, `MYSQL_PORT` (3307) | Có thể tái sử dụng `MYSQL_ROOT_PASSWORD` cho connection string của API, không cần lặp password |
| Image API không chứa connection string, mặc định `Production` | Compose phải truyền `ConnectionStrings__DefaultConnection`. Muốn Swagger thì đặt `ASPNETCORE_ENVIRONMENT=Development` |
| App không tự chạy migration | Volume mới tinh (clone repo trên máy khác) sẽ có DB **rỗng**, lúc đó API trả lỗi 500. Xem quyết định D |
| Image `aspnet:10.0` không có `curl`/`wget` | Healthcheck cho service `api` không dùng được `curl`. Xem quyết định E |

---

## 3. Các quyết định cần chọn

### Quyết định A: Tổ chức file compose

| | A1. Thêm `api` vào `docker-compose.yml` hiện có | A2. Thêm vào cùng file, nhưng gắn `profiles: [api]` | A3. File riêng `docker-compose.api.yml` |
|---|---|---|---|
| Lệnh chạy tất cả | `docker compose up -d` | `docker compose --profile api up -d` | `docker compose -f docker-compose.yml -f docker-compose.api.yml up -d` |
| Lệnh chỉ chạy MySQL (khi dev bằng `dotnet run`) | `docker compose up -d mysql` | `docker compose up -d` | `docker compose up -d` |
| Ưu điểm | Đơn giản nhất, một file nhìn thấy toàn bộ hệ thống | Hành vi cũ giữ nguyên: ai chỉ cần MySQL không phải build API | Tách bạch, file gốc không đổi |
| Nhược điểm | `docker compose up -d` giờ build và chạy cả API, chiếm cổng 8080, dù bạn đang `dotnet run` | Thêm khái niệm *profile*. Dễ quên `--profile` rồi thắc mắc vì sao API không chạy | Lệnh dài, phải hiểu cơ chế *merge* nhiều file compose |

**Đề xuất: A1.** Mục tiêu là học cách nhiều service phối hợp với nhau, nên nhìn toàn bộ trong một file là rõ nhất. Khi chỉ cần MySQL, chạy `docker compose up -d mysql` là đủ. Nếu sau này thấy bất tiện, chuyển sang A2 chỉ cần thêm một dòng `profiles`.

### Quyết định B: Truyền connection string cho API

| | B1. Ghép trong compose từ `.env` | B2. File env riêng cho API (`env_file: api.env`) | B3. Docker secrets |
|---|---|---|---|
| Cách làm | `ConnectionStrings__DefaultConnection: "Server=mysql;Port=3306;Database=roomsplit;User=root;Password=${MYSQL_ROOT_PASSWORD};"` | Viết cả connection string vào `api.env` (git-ignored) | Password nằm trong file, mount vào `/run/secrets/...`, app đọc file |
| Ưu điểm | Password **chỉ có một nơi** (`.env`). Đổi password MySQL thì API tự theo | Tách cấu hình API khỏi MySQL | Password không nằm trong biến môi trường (`docker inspect` không thấy) |
| Nhược điểm | Password vẫn thấy được qua `docker inspect` (chấp nhận được cho dev) | **Lặp** password ở hai nơi, dễ lệch nhau. Thêm một file git-ignored và một file mẫu | Phải sửa code (`AddKeyPerFile` hoặc đọc file). Quá mức cần thiết cho dev |

**Đề xuất: B1**, dùng user `root` cho môi trường dev. Tạo một MySQL user riêng cho app (quyền hẹp hơn root) là thực hành tốt, nhưng với volume hiện tại phải tạo bằng SQL thủ công, trái quy tắc "không đổi DB thủ công". Để dành cho khi làm môi trường production.

### Quyết định C: Chờ MySQL sẵn sàng

| | C1. `depends_on` + `condition: service_healthy` | C2. `depends_on` thường + bật `EnableRetryOnFailure()` trong EF |
|---|---|---|
| Ưu điểm | Không sửa code. Tận dụng healthcheck có sẵn của `mysql` | Bền hơn khi MySQL khởi động lại giữa chừng (không chỉ lúc mới start) |
| Nhược điểm | Chỉ bảo vệ lúc khởi động. Nếu MySQL restart sau đó, request lỗi trong vài giây | Sửa `Program.cs`. Retry có ràng buộc với transaction tự quản lý (phải dùng execution strategy), sẽ ảnh hưởng tới Expenses API sắp làm |

**Đề xuất: C1.** Đây là tính năng chuẩn của compose, không đụng tới code. Việc API khởi động trước MySQL cũng không làm app crash: app chỉ mở kết nối khi có request. Vì vậy C1 chủ yếu giúp tránh lỗi ở những request đầu tiên.

### Quyết định D: Migration cho DB trong compose

| | D1. Không xử lý trong compose | D2. App tự `Database.Migrate()` khi khởi động | D3. Service `migrator` chạy một lần (EF migration bundle) |
|---|---|---|---|
| Cách làm | Chạy `dotnet ef database update` từ máy host (qua cổng 3307), giống hiện nay | Gọi `db.Database.Migrate()` trong `Program.cs` (có thể chỉ khi Development) | Thêm stage Dockerfile tạo `efbundle`, service `migrator` chạy rồi thoát, `api` chờ `service_completed_successfully` |
| Ưu điểm | Không thêm gì. DB hiện tại đã đủ migration | Một lệnh `up` là có DB đầy đủ | Cách được Microsoft khuyến nghị cho production: tách migration khỏi app, dễ kiểm soát |
| Nhược điểm | Máy mới phải chạy thêm một lệnh `dotnet ef`, và phải cài .NET SDK trên host | Nhiều instance API cùng migrate một lúc sẽ tranh chấp. App cần quyền DDL. Microsoft khuyên **không** làm vậy ở production | Phức tạp: thêm stage Dockerfile, cài `dotnet-ef` trong image SDK, thêm service. Nhiều khái niệm mới cùng lúc |

**Đề xuất: D1 ở bước này**, và ghi lại D3 là **bước học tiếp theo**. DB hiện tại đã đủ migration. Gộp D3 vào bây giờ sẽ trộn hai chủ đề (networking và migration bundle) trong cùng một bước.

### Quyết định E: Healthcheck cho service `api`

| | E1. Chưa có healthcheck | E2. Cài `curl` vào image runtime |
|---|---|---|
| Cách làm | Không khai báo | Thêm `RUN apt-get install curl` vào stage final, rồi khai báo healthcheck `curl -f http://localhost:8080/health/live` |
| Ưu điểm | Đơn giản, image không đổi | `docker ps` hiện `(healthy)`, service khác có thể `depends_on` API |
| Nhược điểm | `docker ps` chỉ hiện `Up`, không biết app có thật sự sẵn sàng không | Image to thêm vài MB, tăng bề mặt tấn công. Phải chạy `apt-get` bằng root trước `USER` |

**Đề xuất: E1.** Chưa có service nào phụ thuộc vào API. Có thể tự kiểm tra bằng `curl http://localhost:8080/health/ready` từ máy host.

---

## 4. Thiết kế đề xuất (A1 + B1 + C1 + D1 + E1)

### 4.1. `docker-compose.yml` (dự kiến)

```yaml
# Local development stack: MySQL + RoomSplit API.
#   cp .env.example .env   (then set MYSQL_ROOT_PASSWORD)
#   docker compose up -d --build     → MySQL + API (http://localhost:8080/swagger)
#   docker compose up -d mysql       → MySQL only (when running the API with dotnet run)
# If MySQL is already installed locally on 3306, set MYSQL_PORT=3307 in .env
# and use Port=3307 in the connection string.
services:
  mysql:
    # ... giữ nguyên như hiện tại ...

  api:
    build: .
    image: roomsplit-api:dev
    container_name: roomsplit-api
    restart: unless-stopped
    depends_on:
      mysql:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # "mysql" is the service name, resolved by Docker's internal DNS.
      # 3306 is the port inside the network, not the host-mapped MYSQL_PORT.
      ConnectionStrings__DefaultConnection: "Server=mysql;Port=3306;Database=roomsplit;User=root;Password=${MYSQL_ROOT_PASSWORD};"
    ports:
      - "${API_PORT:-8080}:8080"

volumes:
  mysql-data:
```

Giải thích:

| Dòng | Ý nghĩa |
|---|---|
| `build: .` | Build image từ `Dockerfile` ở thư mục gốc (context `.`), giống `docker build .` |
| `image: roomsplit-api:dev` | Đặt tên cho image vừa build, cùng tên với image ở bước 1 |
| `container_name: roomsplit-api` | Tên cố định, giống quy ước `roomsplit-mysql`, để dùng `docker logs roomsplit-api` |
| `restart: unless-stopped` | Tự khởi động lại nếu app crash hoặc Docker Desktop khởi động lại, trừ khi bạn chủ động `stop` |
| `${MYSQL_ROOT_PASSWORD}` | Compose tự đọc `.env` ở cùng thư mục và thay giá trị vào |
| `${API_PORT:-8080}` | Cổng trên máy host, mặc định 8080, đổi được trong `.env` |

### 4.2. `.env.example`: thêm

```dotenv
# Host port for the API container (docker compose).
API_PORT=8080
```

### 4.3. Kiểm tra (định nghĩa "xong" của bước 2)

```powershell
# 1. Build và chạy cả stack
docker compose up -d --build

# 2. Trạng thái: mysql healthy, api Up
docker compose ps

# 3. API kết nối được MySQL qua network nội bộ
curl http://localhost:8080/health/ready          # → Healthy
curl http://localhost:8080/api/v1/categories      # → 6 category seed

# 4. Swagger UI (Development)
#    http://localhost:8080/swagger

# 5. Quan sát network và DNS nội bộ
docker network inspect roomsplit_default --format '{{range .Containers}}{{.Name}} {{.IPv4Address}}{{println}}{{end}}'
docker exec roomsplit-api getent hosts mysql      # → IP của container mysql

# 6. Thứ tự khởi động: dừng hết rồi up lại, xem log, api chỉ start sau khi mysql healthy
docker compose down          # KHÔNG thêm -v (sẽ xóa volume dữ liệu)
docker compose up -d
docker compose logs api

# 7. dotnet run trên host vẫn dùng được song song (cổng 3307 không đổi)
```

> ⚠️ `docker compose down -v` **xóa cả volume** `mysql-data`, tức mất toàn bộ dữ liệu. Chỉ dùng `down` (không `-v`).

---

## 5. Ảnh hưởng tới phần còn lại của repo

| File | Thay đổi |
|---|---|
| `docker-compose.yml` | Thêm service `api`, cập nhật comment đầu file |
| `.env.example` | Thêm `API_PORT` |
| `.env` (local, git-ignored) | Không bắt buộc sửa, vì `API_PORT` có giá trị mặc định |
| `README.md` | Sửa mục "Run in Docker": dùng `docker compose up -d --build`, bỏ ghi chú "not wired up yet" |
| `CLAUDE.md` | Cập nhật Progress |
| `Dockerfile`, code C# | Không đổi |

---

## 6. Tóm tắt đề xuất

| Quyết định | Đề xuất | Lý do chính |
|---|---|---|
| A. Tổ chức file | **A1**: thêm `api` vào `docker-compose.yml` | Một file thấy toàn bộ hệ thống, dễ học |
| B. Connection string | **B1**: ghép từ `.env`, user `root` | Password chỉ ở một nơi, không sửa code |
| C. Chờ MySQL | **C1**: `condition: service_healthy` | Tính năng chuẩn của compose, healthcheck đã có |
| D. Migration | **D1**: chạy `dotnet ef` từ host | DB đã đủ migration. D3 (migration bundle) để làm bước học sau |
| E. Healthcheck API | **E1**: chưa có | Chưa service nào phụ thuộc API, tránh cài thêm `curl` |

---

## 7. Kết quả triển khai

`docker-compose.yml` có thêm service `api` giống thiết kế ở mục 4.1, nhưng **không có comment**, để khớp với file hiện tại (phần comment đầu file đã được bỏ). `.env.example` có thêm `API_PORT=8080`.

Kết quả chạy các bước kiểm tra ở mục 4.3:

| # | Kiểm tra | Kết quả |
|---|---|---|
| 1 | `docker compose up -d --build` | ✅ Log: `roomsplit-mysql Waiting` → `Healthy` → `roomsplit-api Starting` |
| 2 | `docker compose ps` | ✅ `roomsplit-mysql Up (healthy)` trên 3307, `roomsplit-api Up` trên 8080 |
| 3 | `GET /health/ready` | ✅ `200 Healthy`: API kết nối được MySQL qua `Server=mysql;Port=3306` |
| 3b | `GET /api/v1/categories` | ✅ Trả về đủ 6 category seed |
| 4 | `GET /swagger/index.html` | ✅ `200` (môi trường `Development`) |
| 5 | `docker network inspect roomsplit_default` | ✅ `roomsplit-mysql 172.20.0.2`, `roomsplit-api 172.20.0.3` |
| 5b | `docker exec roomsplit-api getent hosts mysql` | ✅ `172.20.0.2 mysql`: DNS nội bộ phân giải tên service thành IP |
| 6 | `docker compose down` rồi `up -d` | ✅ Compose xóa container và network rồi tạo lại. Volume `roomsplit_mysql-data` **vẫn còn**, dữ liệu (rooms, members) giữ nguyên. API vẫn chỉ start sau khi MySQL healthy |
| 7 | `dotnet run` song song trên host | Chưa chạy thử. Cổng 3307 không đổi nên connection string của host không bị ảnh hưởng |

**Quan sát ngoài dự kiến:** ở lần `up --build` đầu tiên, compose **tạo lại** container `roomsplit-mysql`, dù cấu hình service `mysql` không đổi. Container cũ đã chạy trước đó 17 phút, container mới được tạo lúc 14:47:55. Dữ liệu vẫn còn vì nằm trong volume. Đây chính là minh họa thực tế cho [docker-basics §1.2](docker-basics.md#12-container): **container là thứ dùng xong bỏ, dữ liệu phải nằm trong volume**.

Log API vẫn có warning `Failed to determine the https port for redirect`, như đã dự đoán ở [docker.md](docker.md).

Bước học tiếp theo có thể làm: **D3**, migration bundle chạy trong service `migrator`, để một volume mới tinh cũng tự có schema.

---

## 8. Đọc hiểu file `docker-compose.yml`

Mục này giải thích **từng dòng** của file [docker-compose.yml](../docker-compose.yml) đang dùng, dành cho người mới làm quen với Docker.

### 8.1. Bức tranh tổng thể

Hãy tưởng tượng bạn mở một quán cà phê cần hai nhân viên: **pha chế** (MySQL, giữ dữ liệu) và **thu ngân** (API, nhận yêu cầu từ khách). File `docker-compose.yml` giống **bản phân công công việc**: ai làm gì, ngồi quầy nào, ai phải đợi ai, và cất đồ ở đâu.

Không có compose, bạn phải gõ từng lệnh `docker run` rất dài cho mỗi container. Có compose thì chỉ cần một lệnh:

```powershell
docker compose up -d --build
```

File có 3 phần chính:

```yaml
services:        # danh sách các "nhân viên" (container)
  mysql: ...     #   nhân viên 1: cơ sở dữ liệu
  api: ...       #   nhân viên 2: API RoomSplit

volumes:         # "tủ đồ" lưu dữ liệu lâu dài
  mysql-data:
```

### 8.2. Service `mysql` (cơ sở dữ liệu)

#### `image`

```yaml
  mysql:
    image: mysql:8.0
```

**Dùng image có sẵn** `mysql:8.0` từ Docker Hub. Mình không tự build mà tải image chính thức về dùng luôn.

#### `container_name`

```yaml
    container_name: roomsplit-mysql
```

Đặt **tên cố định** cho container. Nếu không đặt, compose tự đặt tên kiểu `roomsplit-mysql-1`. Có tên cố định thì gõ lệnh dễ hơn, ví dụ `docker logs roomsplit-mysql`.

#### `restart`

```yaml
    restart: unless-stopped
```

**Tự khởi động lại** nếu MySQL bị crash hoặc khi bạn khởi động lại Docker Desktop. Ngoại lệ duy nhất là khi bạn tự tay `stop` nó.

#### `environment`

```yaml
    environment:
      MYSQL_ROOT_PASSWORD: ${MYSQL_ROOT_PASSWORD:?Set MYSQL_ROOT_PASSWORD in .env}
      MYSQL_DATABASE: roomsplit
      TZ: Asia/Ho_Chi_Minh
```

Đây là các **biến môi trường**, tức các "lời dặn" truyền vào container:

| Biến | Ý nghĩa |
|---|---|
| `MYSQL_ROOT_PASSWORD` | Mật khẩu của user `root`. Giá trị lấy từ file `.env` |
| `MYSQL_DATABASE` | Tự tạo database tên `roomsplit` ở **lần chạy đầu tiên** (khi volume còn rỗng) |
| `TZ` | Múi giờ Việt Nam cho MySQL |

> 💡 Cú pháp `${TÊN:?thông báo}`: lấy giá trị biến `TÊN` từ `.env`, **nếu không có thì dừng lại và báo lỗi** "Set MYSQL_ROOT_PASSWORD in .env". Nhờ vậy bạn không vô tình chạy MySQL mà không có mật khẩu.

#### `command`

```yaml
    command: --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci
```

Thêm tham số khi khởi động MySQL. Ở đây là dùng bảng mã **utf8mb4**, để lưu được **tiếng Việt có dấu** (và cả emoji) mà không bị lỗi font.

#### `ports`

```yaml
    ports:
      - "${MYSQL_PORT:-3306}:3306"
```

**Mở cổng** để các công cụ trên máy bạn kết nối được vào MySQL. Định dạng là `"cổng máy bạn:cổng trong container"`.

- `${MYSQL_PORT:-3306}`: lấy `MYSQL_PORT` trong `.env`, **nếu không có thì dùng 3306**. File `.env` của bạn đặt 3307, vì cổng 3306 đã bị MySQL cài trên Windows chiếm.
- Ví dụ thực tế: `"3307:3306"` có nghĩa DBeaver hoặc `dotnet ef` trên máy bạn gọi `localhost:3307`, và Docker chuyển tiếp vào cổng 3306 trong container.

Ba cách đọc biến từ `.env`:

| Cú pháp | Khi biến không có trong `.env` |
|---|---|
| `${TÊN}` | Thay bằng chuỗi rỗng (kèm cảnh báo) |
| `${TÊN:-mặc định}` | Dùng giá trị mặc định |
| `${TÊN:?thông báo}` | Báo lỗi và dừng |

#### `volumes` (bên trong service)

```yaml
    volumes:
      - mysql-data:/var/lib/mysql
```

⭐ **Đây là dòng quan trọng nhất của service này.** MySQL lưu dữ liệu tại `/var/lib/mysql` bên trong container. Mà container thì **có thể bị xóa và tạo lại bất cứ lúc nào** (đã xảy ra thật khi triển khai, xem [mục 7](#7-kết-quả-triển-khai)). Dòng này nối thư mục đó ra **volume** `mysql-data`, một "tủ đồ" nằm ngoài container.

> Container giống như **nhân viên**: có thể thay người. Volume giống như **cái tủ**: người mới đến vẫn mở ra thấy đồ cũ.

#### `healthcheck`

```yaml
    healthcheck:
      test: ["CMD", "mysqladmin", "ping", "-h", "localhost", "-p$${MYSQL_ROOT_PASSWORD}"]
      interval: 10s
      timeout: 5s
      retries: 5
```

**Kiểm tra sức khỏe**: cứ mỗi 10 giây Docker hỏi MySQL "còn sống không?" bằng lệnh `mysqladmin ping`.

| Thuộc tính | Ý nghĩa |
|---|---|
| `test` | Lệnh kiểm tra, chạy **bên trong** container |
| `interval: 10s` | Kiểm tra mỗi 10 giây |
| `timeout: 5s` | Sau 5 giây không trả lời thì tính là một lần thất bại |
| `retries: 5` | Thất bại 5 lần liên tiếp thì đánh dấu `unhealthy` |

Khi ping thành công, `docker compose ps` hiện `(healthy)`.

> 💡 Vì sao lại là `$$`? Trong file compose, `$` dùng để đọc biến từ `.env`. Viết `$$` nghĩa là "để nguyên ký tự `$`". Biến `$MYSQL_ROOT_PASSWORD` vì thế sẽ được đọc **bên trong container** lúc chạy lệnh, chứ không bị compose thay giá trị trước.

### 8.3. Service `api` (API RoomSplit)

#### `build` và `image`

```yaml
  api:
    build: .
    image: roomsplit-api:dev
```

Khác với `mysql`, image này **tự build** từ `Dockerfile` ở thư mục hiện tại (`.`). Image build xong được đặt tên `roomsplit-api:dev`.

- `docker compose up -d --build`: **build lại** image (nhanh nhờ cache layer) rồi chạy.
- Không có `--build`: compose dùng image đã có sẵn, nên code mới sửa **chưa** được đưa vào.

#### `container_name` và `restart`

```yaml
    container_name: roomsplit-api
    restart: unless-stopped
```

Giống phần `mysql`: tên cố định và tự khởi động lại.

#### `depends_on`

```yaml
    depends_on:
      mysql:
        condition: service_healthy
```

**"Đợi MySQL khỏe rồi mới khởi động API."** Compose sẽ:

1. bật `mysql`,
2. chờ healthcheck báo `healthy`,
3. rồi mới bật `api`.

Bạn đã thấy đúng thứ tự này trong log:

```
Container roomsplit-mysql Waiting
Container roomsplit-mysql Healthy
Container roomsplit-api Starting
```

| Cách viết | Compose đợi tới khi |
|---|---|
| `depends_on: [mysql]` | Container MySQL **bật lên**, có thể MySQL chưa sẵn sàng nhận kết nối |
| `condition: service_healthy` | MySQL **thật sự sẵn sàng** (healthcheck thành công) |

#### `environment`

```yaml
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__DefaultConnection: "Server=mysql;Port=3306;Database=roomsplit;User=root;Password=${MYSQL_ROOT_PASSWORD};"
```

| Biến | Ý nghĩa |
|---|---|
| `ASPNETCORE_ENVIRONMENT: Development` | Chạy ở chế độ Development nên **Swagger UI được bật** |
| `ConnectionStrings__DefaultConnection` | Thông tin để API kết nối MySQL. Dấu `__` tương ứng với dấu `:` trong `appsettings.json` |

⚠️ **Phần dễ gây bối rối nhất** nằm trong connection string:

| Phần | Vì sao |
|---|---|
| `Server=mysql` | **Không phải `localhost`**. Trong container API, `localhost` là chính container API. `mysql` là **tên service**, và Docker có "danh bạ" nội bộ (DNS) tra tên này ra IP của container MySQL (`172.20.0.2` như đã kiểm tra) |
| `Port=3306` | **Không phải 3307**. Cổng 3307 chỉ dành cho máy bạn. Hai container nói chuyện **trực tiếp** với nhau qua cổng bên trong là 3306 |
| `Password=${MYSQL_ROOT_PASSWORD}` | Lấy cùng mật khẩu từ `.env`. Mật khẩu chỉ nằm **một nơi**, đổi một lần là cả hai service cùng theo |

Hình dung như sau:

```
       Máy của bạn (Windows)
   localhost:3307          localhost:8080
        │                        │
 ┌──────┼──── mạng nội bộ "roomsplit_default" ───┼──────┐
 │      ▼                        ▼               │
 │  ┌─────────┐   mysql:3306  ┌─────────┐        │
 │  │  mysql  │◀──────────────│   api   │        │
 │  └─────────┘               └─────────┘        │
 └───────────────────────────────────────────────┘
```

Compose **tự tạo** mạng `roomsplit_default` và cho cả hai service vào đó. Bạn không phải khai báo gì.

#### `ports`

```yaml
    ports:
      - "${API_PORT:-8080}:8080"
```

Mở cổng để bạn gọi API từ trình duyệt: `http://localhost:8080/swagger`.

- **Vế phải** luôn là `8080`, vì app trong container lắng nghe cổng đó.
- **Vế trái** là `API_PORT` trong `.env`, mặc định 8080. Nếu cổng 8080 trên máy bạn bị chiếm, đặt `API_PORT=5000` và gọi `localhost:5000`.

### 8.4. `volumes` ở cuối file

```yaml
volumes:
  mysql-data:
```

**Khai báo** volume `mysql-data` để service `mysql` dùng. Compose tạo nó với tên đầy đủ `roomsplit_mysql-data`, có tiền tố là tên project (tên thư mục). Xem bằng `docker volume ls`.

### 8.5. Những lệnh cần nhớ

| Lệnh | Làm gì |
|---|---|
| `docker compose up -d --build` | Build API và chạy cả hai service ở chế độ nền (`-d`) |
| `docker compose up -d mysql` | Chỉ chạy MySQL, dùng khi bạn chạy API bằng `dotnet run` |
| `docker compose ps` | Xem trạng thái các service |
| `docker compose logs -f api` | Xem log API theo thời gian thực (`Ctrl+C` để thoát) |
| `docker compose stop` | Tạm dừng, container vẫn còn |
| `docker compose down` | Dừng và **xóa container và network**. Dữ liệu vẫn còn trong volume |
| `docker compose down -v` | ⚠️ Xóa **cả volume**, tức **mất toàn bộ dữ liệu** |

### 8.6. Tóm tắt trong một đoạn

File này mô tả hai container. **`mysql`** dùng image có sẵn, lưu dữ liệu vào volume để không bị mất, và tự báo sức khỏe qua healthcheck. **`api`** được build từ Dockerfile của mình, đợi MySQL khỏe rồi mới chạy, và kết nối tới MySQL bằng **tên service `mysql`, cổng 3306** qua mạng nội bộ do compose tự tạo. Mọi giá trị bí mật hoặc hay thay đổi (mật khẩu, cổng) đều lấy từ file `.env`, không ghi cứng trong file compose.

---

## 9. Cơ chế compose đọc file `.env`

### 9.1. Ý tưởng chính: "điền vào chỗ trống" (interpolation)

File `docker-compose.yml` giống một **mẫu đơn có chỗ trống** `${...}`. File `.env` là **tờ giấy ghi sẵn câu trả lời**. Trước khi làm bất cứ việc gì, compose **điền câu trả lời vào chỗ trống**, rồi mới dùng file đã điền xong để tạo container.

```
 docker-compose.yml                .env                    Kết quả compose dùng
┌───────────────────────────┐   ┌──────────────────┐   ┌───────────────────────┐
│ ports:                    │   │ MYSQL_PORT=3307  │   │ ports:                │
│   - "${MYSQL_PORT:-3306}  │ + │ API_PORT=...     │ = │   - "3307:3306"       │
│        :3306"             │   │ MYSQL_ROOT_...   │   │                       │
└───────────────────────────┘   └──────────────────┘   └───────────────────────┘
```

Cơ chế này gọi là **variable interpolation** (thay biến).

### 9.2. Từng bước compose làm khi chạy `docker compose up`

1. **Tìm thư mục project.** Mặc định đó là thư mục chứa `docker-compose.yml` (ở đây là `D:\Workspace\RoomSplit`).
2. **Tìm file `.env` trong thư mục đó.** Tên phải đúng là `.env`. Compose không tự tìm `.env.example` hay `api.env`.
3. **Đọc từng dòng `TÊN=GIÁ_TRỊ`.** Dòng bắt đầu bằng `#` là comment, dòng trống bị bỏ qua.
4. **Đọc biến môi trường của terminal** đang chạy lệnh.
5. **Thay mọi `${...}` trong file YAML** bằng giá trị tìm được, theo thứ tự ưu tiên ở 9.3.
6. **Kiểm tra cú pháp** (`:?` thiếu giá trị thì báo lỗi và dừng ở đây).
7. Dùng file YAML đã thay xong để **build image và tạo container**.

Bước 5 xảy ra **trên máy bạn, trước khi có container nào**. Container không biết gì về file `.env`.

### 9.3. Thứ tự ưu tiên: ai thắng khi trùng tên?

| Ưu tiên | Nguồn | Ví dụ |
|---|---|---|
| 1 (cao nhất) | Biến môi trường của **terminal** | `$env:MYSQL_PORT=4000` (PowerShell) |
| 2 | File `.env` (hoặc file chỉ định bằng `--env-file`) | `MYSQL_PORT=3307` |
| 3 (thấp nhất) | Giá trị mặc định trong YAML | `${MYSQL_PORT:-3306}` → 3306 |

Kiểm tra thực tế (2026-10-21):

```powershell
docker compose config                       # mysql ports: 3307:3306  ← lấy từ .env
$env:MYSQL_PORT=4000; docker compose config # mysql ports: 4000:3306  ← terminal thắng .env
Remove-Item Env:MYSQL_PORT                  # dọn biến tạm
```

> 💡 Nếu một ngày compose "không chịu" dùng giá trị trong `.env`, hãy kiểm tra terminal có đang đặt biến cùng tên không.

### 9.4. Các cú pháp thay biến

| Cú pháp | Biến có giá trị | Biến **không có** | Biến **rỗng** (`TÊN=`) |
|---|---|---|---|
| `${TÊN}` | giá trị | chuỗi rỗng + cảnh báo | chuỗi rỗng |
| `${TÊN:-mặc định}` | giá trị | `mặc định` | `mặc định` |
| `${TÊN-mặc định}` | giá trị | `mặc định` | chuỗi rỗng |
| `${TÊN:?lỗi}` | giá trị | **báo lỗi, dừng** | **báo lỗi, dừng** |
| `${TÊN?lỗi}` | giá trị | **báo lỗi, dừng** | chuỗi rỗng |
| `$$` | ký tự `$` (không thay) | | |

Dấu `:` nghĩa là **"coi giá trị rỗng như không có"**.

Kiểm tra thực tế: `.env.example` có dòng `MYSQL_ROOT_PASSWORD=` (rỗng). Chạy compose với file này sẽ bị chặn ngay:

```powershell
docker compose --env-file .env.example config
# error while interpolating services.mysql.environment.MYSQL_ROOT_PASSWORD:
#   required variable MYSQL_ROOT_PASSWORD is missing a value: Set MYSQL_ROOT_PASSWORD in .env
```

### 9.5. ⚠️ `.env` KHÔNG tự đi vào container

Đây là điểm người mới hay nhầm nhất. Biến trong `.env` **chỉ dùng để điền vào file YAML**. Biến nào muốn có **bên trong container** thì phải được khai báo trong `environment:` của service đó.

Kiểm tra thực tế: trong container `api`, `printenv` chỉ có các biến được khai báo trong `environment:`:

```
ConnectionStrings__DefaultConnection   ← có, vì khai báo trong environment:
ASPNETCORE_ENVIRONMENT                 ← có, vì khai báo trong environment:
MYSQL_PORT, API_PORT, MYSQL_ROOT_PASSWORD  ← KHÔNG có
```

`MYSQL_ROOT_PASSWORD` không có trong container `api` dưới dạng biến riêng. Giá trị của nó chỉ được **ghép vào** connection string lúc compose điền chỗ trống.

Ba khái niệm hay bị lẫn với nhau:

| | File `.env` (cạnh compose) | `environment:` trong service | `env_file:` trong service |
|---|---|---|---|
| Dùng cho | **Điền `${...}`** trong file YAML | Đặt biến **trong container** | Đặt biến **trong container**, lấy từ một file |
| Compose tự đọc? | ✅ Tự động | Khai báo trực tiếp | Phải khai báo `env_file: api.env` |
| Biến vào container? | ❌ Không | ✅ Có | ✅ Có (toàn bộ file) |
| Đang dùng trong RoomSplit? | ✅ | ✅ | ❌ (phương án B2 không chọn) |

### 9.6. Xem kết quả sau khi thay biến

```powershell
docker compose config            # in toàn bộ file YAML đã điền xong
docker compose config --quiet    # chỉ kiểm tra hợp lệ, không in gì
```

> ⚠️ `docker compose config` in ra **cả mật khẩu thật** (trong `MYSQL_ROOT_PASSWORD` và connection string). Đừng chụp màn hình hay dán kết quả lên chat, issue hay tài liệu.

### 9.7. Vì sao `.env` không được commit

- `.env` chứa mật khẩu thật, nên nằm trong `.gitignore`.
- `.env.example` là **mẫu** (không có mật khẩu) được commit để người khác biết cần những biến nào: `cp .env.example .env` rồi điền.
- Khi thêm biến mới (như `API_PORT`), luôn thêm vào **cả** `.env.example`.
