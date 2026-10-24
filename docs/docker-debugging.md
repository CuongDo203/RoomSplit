# Debug ứng dụng đang chạy trong Docker

Tài liệu học tập. Trả lời câu hỏi: **app chạy bằng Docker bị lỗi thì điều tra theo thứ tự nào, dùng lệnh gì?** Trọng tâm là các lệnh `docker compose logs -f api`, `docker exec -it <container> sh`, và các lệnh `ls`, `cat`, `env`, `ps` dùng bên trong container.

Mọi output trong bài là **output thật**, lấy ngày 2026-10-22 trên project compose tạm `roomsplit-debuglab` (volume mới, cổng 18080, image build từ code hiện tại). Project này đã bị xoá sau khi xong, không đụng DB dev. Mật khẩu trong output đã được che.

Kiến thức nền: [docker-basics.md](docker-basics.md), [docker-compose.md](docker-compose.md), [docker-volumes-networks.md](docker-volumes-networks.md) (network, DNS), [dev-migrate-seed.md](dev-migrate-seed.md) (app tự migrate khi khởi động).

## Mục lục
1. [Quy trình 6 bước](#1-quy-trình-6-bước)
2. [Từng lệnh chi tiết](#2-từng-lệnh-chi-tiết)
3. [Thực hành: 3 kịch bản lỗi thật](#3-thực-hành-3-kịch-bản-lỗi-thật)
4. [Image thiếu công cụ: làm sao kiểm tra mạng?](#4-image-thiếu-công-cụ-làm-sao-kiểm-tra-mạng)
5. [Lệnh bổ trợ](#5-lệnh-bổ-trợ)
6. [Tra cứu theo triệu chứng](#6-tra-cứu-theo-triệu-chứng)
7. [Bảng lệnh nhanh cho RoomSplit](#7-bảng-lệnh-nhanh-cho-roomsplit)

---

## 1. Quy trình 6 bước

Nguyên tắc: **đi từ ngoài vào trong**, từ thông tin rẻ nhất (trạng thái, log) tới tốn công nhất (vào container). Phần lớn lỗi được tìm ra ở bước 2–3.

```
1. Triệu chứng là gì?        HTTP 500? 503? không vào được? container chết?
        │
2. Container còn sống không?  docker compose ps -a          → Up / Restarting / Exited (mã thoát)
        │
3. Log nói gì?                docker compose logs api       → tìm exception ĐẦU TIÊN
        │
4. Cấu hình thực tế ra sao?   docker compose exec api sh    → env, ls, cat, ps
        │
5. Phụ thuộc có ổn không?     DB healthy? DNS? cổng mở?     → getent, /dev/tcp, healthcheck
        │
6. Sửa → tạo lại container → kiểm tra lại (curl /health/ready) → ghi lại nguyên nhân
```

### Bước 1: mô tả triệu chứng thật cụ thể

| Triệu chứng | Gợi ý hướng điều tra |
|---|---|
| Trình duyệt báo "không kết nối được" `localhost:8080` | Container không chạy, hoặc sai `ports`. Bước 2 |
| HTTP 500 | App chạy nhưng gặp exception. Bước 3 |
| `/health/ready` trả 503 | App sống nhưng không tới được DB. Bước 5 |
| Request treo rất lâu | DB chết và EF đang retry (kịch bản B) |
| Lúc chạy lúc không, container "Up 2 seconds" | Crash loop (kịch bản A) |

### Bước 2: xem trạng thái container

```bash
docker compose ps -a        # -a: hiện cả container đã dừng
```

Đọc cột `STATUS`:

| STATUS | Ý nghĩa |
|---|---|
| `Up 2 minutes` | Đang chạy |
| `Up 33 seconds (healthy)` / `(unhealthy)` | Đang chạy và có healthcheck (MySQL có, API chưa có) |
| `Up Less than a second`, rồi `Restarting (139) 2 seconds ago` | **Crash loop**: app chết, Docker khởi động lại liên tục do `restart: unless-stopped` |
| `Exited (0)` | Dừng bình thường (vd `docker compose stop`) |
| `Exited (137)` | Bị kill (SIGKILL). Có thể do hết bộ nhớ: kiểm tra `docker inspect -f '{{.State.OOMKilled}}' <container>` |

**Mã thoát (exit code):** `0` là thoát bình thường. `1–127` là app tự thoát với mã lỗi. Từ `128` trở lên nghĩa là process bị dừng bởi signal, số signal = mã − 128: `137` = 128+9 (SIGKILL), `143` = 128+15 (SIGTERM). Trong kịch bản A, .NET gặp exception không bắt được và thoát với `139`.

---

## 2. Từng lệnh chi tiết

### 2.1. `docker compose logs -f api`: đọc log

Docker lưu lại mọi thứ app ghi ra **stdout/stderr**. ASP.NET Core mặc định ghi log ra console, nên log của app nằm ở đây.

| Phần | Ý nghĩa |
|---|---|
| `docker compose logs` | Log của các service trong project (chạy ở thư mục chứa `docker-compose.yml`) |
| `api` | Tên **service** (không phải tên container). Bỏ trống = mọi service, ghi thêm nhiều service: `logs api mysql` |
| `-f` (`--follow`) | Bám theo log mới như `tail -f`. **Ctrl+C chỉ dừng xem log**, không dừng container |

Các tuỳ chọn hay dùng:

```bash
docker compose logs --tail 50 api             # chỉ 50 dòng cuối (log dài thì luôn dùng)
docker compose logs -f --tail 0 api           # chỉ xem log MỚI từ bây giờ
docker compose logs --since 10m api           # 10 phút gần nhất (cũng nhận 2026-10-22T16:49:00)
docker compose logs -t api                    # thêm timestamp của Docker vào mỗi dòng
docker compose logs --no-log-prefix api       # bỏ tiền tố "api-1  | " (dễ copy, dễ grep)
docker logs -f roomsplit-api                  # lệnh docker thường: dùng TÊN CONTAINER
```

Output thật (`--tail 5 --timestamps`):

```
api-1  | 2026-10-22T16:49:21.650249117Z       Application started. Press Ctrl+C to shut down.
api-1  | 2026-10-22T16:49:21.651267992Z       Hosting environment: Development
api-1  | 2026-10-22T16:49:21.651278993Z       Content root path: /app
```

Lọc log:

```bash
# Git Bash / Linux
docker compose logs --no-log-prefix api | grep -E "^(fail|crit)|Exception"
```

```powershell
# PowerShell (không có grep)
docker compose logs --no-log-prefix api | Select-String -Pattern "^(fail|crit)|Exception"
```

**Đọc log .NET:** mỗi entry bắt đầu bằng mức độ `info:` / `warn:` / `fail:` / `crit:`. Stack trace rất dài, hãy tìm **dòng `Exception` đầu tiên** (nguyên nhân gốc) và dòng có **file code của mình** (`Program.cs:line 65`), bỏ qua các dòng của framework ở giữa.

**Log tồn tại tới khi nào?**
- Restart (crash loop, `docker compose restart`): log **giữ nguyên**, nối tiếp nhau. Rất tiện để xem lần crash trước.
- Container bị **tạo lại** (`down`/`up`, đổi config rồi `up`): log cũ **mất**. Vì vậy hãy xem và lưu log **trước** khi sửa: `docker compose logs api > api-crash.log`.
- Staging/prod giới hạn log ở `max-size: 10m`, `max-file: 3` ([docker-compose.prod.yml](../docker-compose.prod.yml)), nên log quá cũ bị xoay vòng.

### 2.2. `docker exec -it <container> sh`: vào bên trong container

Mở một shell **bên trong container đang chạy**, để nhìn thấy đúng những gì app nhìn thấy: biến môi trường, file, mạng, DNS.

| Phần | Ý nghĩa |
|---|---|
| `exec` | Chạy **thêm** một process trong container đang chạy (app vẫn chạy bình thường) |
| `-i` (interactive) | Giữ stdin mở để gõ lệnh |
| `-t` (tty) | Cấp terminal giả: có dấu nhắc, màu, phím mũi tên |
| `<container>` | Tên **container**: `roomsplit-api` (dev), `roomsplit-staging-api-1` (staging) |
| `sh` | Lệnh cần chạy, ở đây là shell. Gõ `exit` hoặc Ctrl+D để thoát |

Dạng compose (khuyên dùng vì dùng **tên service**, giống nhau ở mọi môi trường):

```bash
docker compose exec api sh          # tự có -it, không cần gõ
docker compose exec api bash        # image aspnet (Debian) có bash
docker compose exec -u root api sh  # vào bằng root (mặc định là user "app", UID 1654)
```

Không cần mở shell, chạy thẳng một lệnh:

```bash
docker compose exec api env                       # chạy xong thoát luôn
docker compose exec -T api cat /app/appsettings.json   # -T: không cấp TTY, dùng trong script hoặc khi pipe
docker exec roomsplit-api ls -la /app             # lệnh docker thường: bỏ -it khi không cần gõ tương tác
```

Lưu ý:
- **Shell nào có trong image?** Image `aspnet:10.0` (Debian) có `sh` và `bash`. Image Alpine chỉ có `sh`. Image *chiseled/distroless* **không có shell**: xem [mục 4](#4-image-thiếu-công-cụ-làm-sao-kiểm-tra-mạng).
- **Container phải đang chạy.** Đang crash loop thì `exec` chỉ thành công trong vài giây giữa hai lần restart, hoặc báo lỗi container đang restart. Khi đó dùng `docker compose run --entrypoint sh` ([kịch bản C](#kịch-bản-c-container-chết-quá-nhanh-không-exec-được)).
- **Mọi thay đổi bên trong sẽ mất** khi container được tạo lại. Vào container để **điều tra**, sửa thật phải sửa code/config rồi build lại.
- Container chạy bằng user `app` (không phải root, xem [Dockerfile](../Dockerfile)): `whoami` → `app`, `id` → `uid=1654(app)`. Cần quyền root để điều tra thì dùng `-u root`.

### 2.3. `ls`: file có thật sự nằm trong container không?

```bash
docker compose exec api ls -la /app
```

```
-rwxr--r-- 1 root root   28160 Nov 27  2024 EFCore.NamingConventions.dll
-rwxr--r-- 1 root root  857448 Nov 12  2024 MySqlConnector.dll
-rwxr--r-- 1 root root  526848 Aug 17  2025 Pomelo.EntityFrameworkCore.MySql.dll
...
```

Dùng để kiểm tra:
- **Đủ file chưa?** Thiếu DLL, thiếu `appsettings.*.json` → lỗi do `.dockerignore` hoặc bước `COPY`/`publish`.
- **Quyền.** File thuộc `root`, user `app` chỉ đọc được. Đây là **cố ý** (app không sửa được chính nó). App cần ghi file thì phải ghi vào volume hoặc `/tmp`.
- **Bản build nào?** Cột ngày giờ cho biết image có phải bản mới build không.

Phát hiện thật trong lúc thí nghiệm: `/app` **không có** `appsettings.Development.json`, dù file này có trên máy host:

```
$ docker cp roomsplit-debuglab-api-1:/app/appsettings.Development.json -
Error response from daemon: Could not find the file /app/appsettings.Development.json in container ...
```

Lý do: [.dockerignore](../.dockerignore) có dòng `**/appsettings.Development.json` (file chứa mật khẩu, không được đóng vào image). Connection string trong container vì vậy **chỉ** đến từ biến môi trường. Bài học: **file có trên host chưa chắc có trong container**, hãy `ls` để chắc chắn.

### 2.4. `cat`: xem nội dung file

```bash
docker compose exec api cat /app/appsettings.json
docker compose exec api cat /etc/resolv.conf   # DNS container dùng (127.0.0.11 = DNS nhúng của Docker)
docker compose exec api cat /etc/hosts
```

**Lưu ý quan trọng với ASP.NET Core:** `cat appsettings.json` chỉ cho thấy **một lớp** cấu hình. Giá trị thật app dùng được ghép theo thứ tự, lớp sau đè lớp trước:

```
appsettings.json  →  appsettings.{Environment}.json  →  biến môi trường  →  tham số dòng lệnh
```

Ví dụ thật: `appsettings.json` không có `ConnectionStrings`, nhưng app vẫn kết nối được vì biến môi trường `ConnectionStrings__DefaultConnection` cung cấp giá trị đó. Vì vậy luôn xem thêm `env` (mục 2.5).

### 2.5. `env`: biến môi trường app thực sự nhận được

```bash
docker compose exec api env
docker compose exec api sh -c 'env | grep -i connection'
```

Output thật (đã sắp xếp, che mật khẩu):

```
APP_UID=1654
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_HTTP_PORTS=8080
ConnectionStrings__DefaultConnection=Server=mysql;Port=3306;Database=roomsplit;User=root;Password=*****;
DOTNET_RUNNING_IN_CONTAINER=true
DOTNET_VERSION=10.0.13
HOSTNAME=3b881111674f
```

Nguồn gốc từng biến:

| Biến | Đến từ |
|---|---|
| `ASPNETCORE_ENVIRONMENT`, `ConnectionStrings__DefaultConnection` | `environment:` trong file compose |
| `ASPNETCORE_HTTP_PORTS`, `DOTNET_VERSION`, `APP_UID` | Image gốc `aspnet:10.0` (lệnh `ENV` trong Dockerfile của Microsoft) |
| `HOSTNAME` | Docker, bằng 12 ký tự đầu của container ID |

Câu hỏi `env` trả lời được:
- **Đúng môi trường chưa?** `ASPNETCORE_ENVIRONMENT=Development` → tự migrate + seed + Swagger. Sai biến này thì hành vi app khác hẳn.
- **Connection string đúng chưa?** `Server=mysql` (tên service) và `Port=3306` (cổng **trong** network), không phải `localhost` hay `3307`.
- **Biến từ `.env` có vào được không?** Thiếu biến hoặc giá trị rỗng nghĩa là `.env`/`--env-file` không được đọc.
- `__` (hai dấu gạch dưới) trong tên biến tương đương `:` trong JSON: `ConnectionStrings__DefaultConnection` ↔ `ConnectionStrings:DefaultConnection`.

⚠️ **Bảo mật:** `env` in mật khẩu ra màn hình. Che đi trước khi chụp màn hình hay dán vào chat/issue. `docker compose config` (mục 5) cũng in **đầy đủ** `MYSQL_ROOT_PASSWORD` dạng chữ thường.

### 2.6. `ps`: process nào đang chạy trong container?

```bash
docker compose exec api ps aux
```

```
USER       PID %CPU %MEM    VSZ   RSS TTY      STAT START   TIME COMMAND
app          1 23.5  1.7 275001504 132836 ?    Ssl  16:49   0:02 dotnet RoomSplit.Api.dll
app         62 33.3  0.0   7896  3840 ?        Rs   16:49   0:00 ps aux
```

Đọc kết quả:
- **PID 1 phải là app** (`dotnet RoomSplit.Api.dll`). Container sống chừng nào PID 1 còn sống. PID 1 thoát thì container dừng, và mã thoát của nó chính là mã ở `Exited (...)`.
- PID 1 là `dotnet` (không phải `sh -c dotnet ...`) nhờ `ENTRYPOINT ["dotnet", "RoomSplit.Api.dll"]` dạng **mảng JSON**. Nếu viết dạng chuỗi `ENTRYPOINT dotnet RoomSplit.Api.dll`, PID 1 sẽ là `sh`, và `sh` không chuyển tiếp SIGTERM cho app: `docker stop` phải chờ 10 giây rồi kill (`Exited (137)`), app không kịp tắt gọn gàng.
- `ps aux` chính là một process: nó thấy chính nó (PID 62).
- Cột `RSS` là RAM thật đang dùng (132.836 KB ≈ 130 MB lúc khởi động). `VSZ` rất lớn là bình thường với .NET (bộ nhớ ảo được đặt trước, chưa dùng).

Image không có `ps` thì sao? Xem từ **host**, không cần công cụ trong image:

```bash
docker compose top api
```

```
SERVICE  #   UID   PID   PPID  C   STIME  TTY  TIME      CMD
api      1   1654  1447  1422  21  16:49  ?    00:00:02  dotnet RoomSplit.Api.dll
```

PID ở đây (1447) là PID **trên host** (VM của Docker Desktop). Trong container, cùng process đó có PID 1: mỗi container có **PID namespace** riêng, giống network namespace ở [docker-volumes-networks §2.1](docker-volumes-networks.md#21-mỗi-container-có-network-stack-riêng). Cách khác khi không có `ps`: đọc thẳng `/proc`:

```bash
docker compose exec api sh -c 'for p in /proc/[0-9]*; do printf "%s " "${p#/proc/}"; tr "\0" " " < $p/cmdline; echo; done'
# 1 dotnet RoomSplit.Api.dll
```

---

## 3. Thực hành: 3 kịch bản lỗi thật

### Kịch bản A: sai mật khẩu DB, container crash loop

Gây lỗi: cho API một connection string có `Password=wrong`.

**Bước 2: `docker compose ps -a`.** Gọi vài lần sẽ thấy trạng thái nhảy qua lại:

```
STATUS
Up Less than a second
Restarting (139) 2 seconds ago
Up 1 second
Restarting (139) 6 seconds ago
```

Trong 30 giây: `RestartCount=6`. Dấu hiệu nhận biết crash loop: thời gian `Up` không bao giờ lớn, và mã `(139)` lặp đi lặp lại.

```bash
docker inspect roomsplit-api --format 'Status={{.State.Status}} ExitCode={{.State.ExitCode}} RestartCount={{.RestartCount}}'
# Status=restarting ExitCode=139 RestartCount=9
```

**Bước 3: log.** Log của mọi lần restart vẫn còn, lọc ra lỗi:

```
      6 fail: Microsoft.EntityFrameworkCore.Database.Connection[20004]
      6 Unhandled exception. MySqlConnector.MySqlException (0x80004005): Access denied for user 'root'@'172.25.0.3' (using password: YES)
```

Cuối stack trace:

```
   at Microsoft.EntityFrameworkCore.Migrations.Internal.Migrator.MigrateAsync(...)
   at Program.<Main>$(String[] args) in /src/RoomSplit.Api/Program.cs:line 65
```

Đọc được: lỗi ở `MigrateAsync()` khi khởi động (dòng 65 [Program.cs](../RoomSplit.Api/Program.cs)), MySQL từ chối user `root` đến từ IP `172.25.0.3` (container API).
- `using password: YES`: có gửi mật khẩu nhưng **sai**. `NO` nghĩa là mật khẩu rỗng (biến chưa được truyền vào).
- 6 lần `Access denied` = 6 lần restart. Mỗi lần chỉ **1** lỗi, tức `EnableRetryOnFailure` **không** retry lỗi này. Đúng như mong muốn: sai mật khẩu không phải lỗi tạm thời, retry cũng vô ích.

**Bước 4: `env`** để so mật khẩu API nhận được với `MYSQL_ROOT_PASSWORD` của MySQL → khác nhau → sửa `.env`/compose rồi `docker compose up -d api` (compose thấy config đổi nên tạo lại container).

### Kịch bản B: MySQL dừng khi API đang chạy

Gây lỗi: `docker compose stop mysql`.

```
ready=200                                   # trước khi dừng MySQL
 Container roomsplit-debuglab-mysql-1 Stopped
ready=503                                   # /health/ready ngay sau đó
rooms=000 time=60.005821s                   # GET /api/v1/rooms: curl bỏ cuộc sau 60 giây, chưa có phản hồi
```

`docker compose ps -a`:

```
roomsplit-debuglab-api-1     Up 2 minutes
roomsplit-debuglab-mysql-1   Exited (137) 2 minutes ago
```

Log API:

```
     12       MySqlConnector.MySqlException (0x80004005): Unable to connect to any of the specified MySQL hosts.
```

Bài học:
- **Container "Up" không có nghĩa là app khoẻ.** API vẫn `Up`, nhưng mọi request cần DB đều hỏng. `docker compose ps` không biết điều đó vì API chưa có healthcheck (image không có `curl`). Chỉ `/health/ready` (503) mới lộ ra.
- **Request treo thay vì lỗi ngay.** `Unable to connect` là lỗi tạm thời nên `EnableRetryOnFailure` retry tới 6 lần với thời gian chờ tăng dần: request treo hơn 60 giây. Đây là đánh đổi của retry. Nó giúp lúc khởi động, nhưng làm chậm phản hồi khi DB chết hẳn. Xem [dev-migrate-seed.md §4.3](dev-migrate-seed.md#43-ghi-chú).
- Khởi động lại MySQL (`docker compose start mysql`): sau khoảng 15 giây, `/health/ready` về **200**. API tự hồi phục, không cần restart.

### Kịch bản C: container chết quá nhanh, không exec được

Khi app crash ngay lúc khởi động, `exec` không có cơ hội chạy. Cách xử lý: tạo một container **mới**, cùng image, cùng config, nhưng **thay lệnh khởi động bằng shell**:

```bash
docker compose run --rm --no-deps --entrypoint sh api
```

| Tuỳ chọn | Ý nghĩa |
|---|---|
| `run` | Tạo container mới từ định nghĩa service `api` (cùng image, `environment`, network) |
| `--entrypoint sh` | Chạy `sh` thay cho `dotnet RoomSplit.Api.dll`, nên app không khởi động và không crash |
| `--no-deps` | Không khởi động kèm `mysql` |
| `--rm` | Xoá container khi thoát |

Output thật (chạy với `-c '...'` thay cho shell tương tác):

```
inside fresh container: ec7ed9c2b1fa
ASPNETCORE_ENVIRONMENT=Development
/app/appsettings.Production.json
/app/appsettings.Staging.json
/app/appsettings.json
```

Bên trong có thể `env`, `ls`, `cat`, hoặc tự chạy app bằng tay để xem lỗi: `dotnet RoomSplit.Api.dll`.

Cách khác không cần shell: `docker cp <container>:/app/appsettings.json -` đọc được file **cả khi container đã dừng**.

---

## 4. Image thiếu công cụ: làm sao kiểm tra mạng?

Image runtime được giữ nhỏ và ít bề mặt tấn công, nên thiếu nhiều công cụ quen thuộc. Kiểm tra thật trong `roomsplit-api`:

| Có | Không có |
|---|---|
| `sh`, `bash`, `getent`, `ps`, `top` | `curl`, `wget`, `ping`, `nc`, `nslookup` |

**Không cài thêm vào container đang chạy** (`apt-get install`): cần root, mất khi tạo lại container, và làm container khác với image. Thay vào đó:

| Muốn kiểm tra | Cách dùng công cụ có sẵn |
|---|---|
| DNS: tên `mysql` ra IP nào? | `getent hosts mysql` → `172.25.0.2      mysql` |
| Cổng có mở không? | `bash -c 'exec 3<>/dev/tcp/mysql/3306' && echo open` (bash tự mở kết nối TCP) |

Output thật:

```
mysql:3306 open
bash: connect: Connection refused          # thử cổng 3307
mysql:3307 closed
```

**Container "đồ nghề" (sidecar):** chạy một container khác có đủ công cụ, cắm vào **cùng network**:

```bash
# Vào network của project, thấy mọi service theo tên
docker run --rm --network roomsplit_default busybox:1.37 nc -zv mysql 3306
docker run --rm --network roomsplit_default busybox:1.37 nslookup mysql

# Mượn chính network namespace của container API (cùng IP, cùng localhost)
docker run --rm -it --network container:roomsplit-api nicolaka/netshoot   # nên ghi rõ tag phiên bản
```

Cách thứ hai là "Cách 4" ở [docker-volumes-networks §2.8](docker-volumes-networks.md#cách-4-dùng-chung-network-namespace).

---

## 5. Lệnh bổ trợ

| Lệnh | Dùng khi | Ví dụ output thật |
|---|---|---|
| `docker inspect <c> --format '{{.State.Status}} {{.State.ExitCode}} {{.RestartCount}}'` | Xem trạng thái, mã thoát, số lần restart | `restarting 139 9` |
| `docker inspect <c> --format '{{.State.OOMKilled}}'` | `Exited (137)`: có phải hết RAM không | `false` |
| `docker inspect <mysql> --format '{{.State.Health.Status}}'` | Healthcheck của MySQL | `healthy` |
| `docker events --since 30s --until 0s --filter container=<c>` | Lịch sử start/die của container | `die exitCode=139`, `start` |
| `docker stats --no-stream` | CPU/RAM từng container | `api cpu=0.01% mem=76.2MiB`, `mysql mem=351.7MiB` |
| `docker cp <c>:/đường/dẫn -` | Lấy file ra, cả khi container đã dừng | |
| `docker compose config` | Xem file compose **sau khi** ghép override và thay biến `.env` | ⚠️ in **đầy đủ** mật khẩu |
| `docker compose exec -u root api sh` | Cần quyền root để điều tra | `uid=0(root)` |

---

## 6. Tra cứu theo triệu chứng

| Triệu chứng | Lệnh đầu tiên | Nguyên nhân hay gặp |
|---|---|---|
| `Restarting (...)` liên tục | `docker compose logs --tail 100 api` | Exception lúc khởi động: sai connection string, migrate lỗi, thiếu config |
| `Exited (137)` | `docker inspect -f '{{.State.OOMKilled}}'` | Hết RAM (`mem_limit` ở prod), hoặc bị `docker kill` |
| `Up` nhưng `/health/ready` 503 | `docker compose ps mysql`, `getent hosts mysql` | MySQL chết/unhealthy, sai tên host |
| Log có `Access denied ... (using password: NO)` | `docker compose exec api env` | Biến mật khẩu không được truyền vào (`.env` không được đọc) |
| Log có `Unable to connect to any of the specified MySQL hosts` | `getent hosts mysql`, `/dev/tcp/mysql/3306` | MySQL chưa sẵn sàng, sai `Server=`/`Port=`, khác network |
| Không vào được `localhost:8080` dù container `Up` | `docker compose ps` (cột `PORTS`) | Thiếu `ports`, sai cổng host, cổng bị chiếm |
| Sửa code nhưng không thấy thay đổi | `docker compose exec api ls -la /app` (xem ngày giờ) | Quên `--build`, hoặc đang chạy image tag cũ |
| Config không như mong đợi | `env` + `cat appsettings*.json` | Sai `ASPNETCORE_ENVIRONMENT`, biến môi trường đè giá trị JSON |

---

## 7. Bảng lệnh nhanh cho RoomSplit

Dev (chạy ở thư mục gốc repo):

```bash
docker compose ps -a
docker compose logs -f --tail 100 api
docker compose logs --since 10m mysql
docker compose exec api sh                 # hoặc bash
docker compose exec api env
docker compose exec api ls -la /app
docker compose exec api cat /app/appsettings.json
docker compose exec api ps aux
docker compose top api
docker compose exec api getent hosts mysql
curl http://localhost:8080/health/ready    # từ host
```

Staging/production: thêm các file và env file, mọi lệnh con giữ nguyên:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/staging.env logs -f --tail 100 api
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/staging.env exec api sh
```

Tóm tắt một dòng: **`ps -a` để biết container còn sống không, `logs` để biết vì sao, `exec` + `env`/`ls`/`cat`/`ps` để xác nhận container thật sự nhận được gì.** Đừng đoán: kiểm tra từ bên trong.
