# Docker volume và network: tìm hiểu sâu, áp dụng vào RoomSplit

Tài liệu học tập. Mọi kết quả ghi trong bài được kiểm tra thực tế trên máy dev (Docker Engine 29.7.2, Docker Desktop trên WSL2) ngày 2026-10-21 và 2026-10-22, bằng container thử nghiệm `busybox:1.37` và `mysql:8.0.46`. Chi tiết ở [mục 3](#3-kết-quả-thí-nghiệm).

> 🆕 **2026-10-22:** thêm [mục 2.9: network Docker tạo sẵn và network do user tạo](#29-đi-sâu-network-docker-tạo-sẵn-và-network-do-user-tạo).

Kiến thức nền: [docker-basics.md](docker-basics.md) (image, container, layer, copy-on-write), [docker-compose.md](docker-compose.md) (compose của RoomSplit).

## Mục lục
1. [Volume: dữ liệu sống lâu hơn container](#1-volume-dữ-liệu-sống-lâu-hơn-container)
2. [Network: container nói chuyện với nhau thế nào](#2-network-container-nói-chuyện-với-nhau-thế-nào)
3. [Kết quả thí nghiệm](#3-kết-quả-thí-nghiệm)
4. [Áp dụng vào RoomSplit](#4-áp-dụng-vào-roomsplit)
5. [Bảng lệnh tra cứu nhanh](#5-bảng-lệnh-tra-cứu-nhanh)

---

## 1. Volume: dữ liệu sống lâu hơn container

### 1.1. Vấn đề cần giải quyết

Theo [docker-basics §1.3](docker-basics.md#13-layer-kiến-trúc-nhiều-lớp), mỗi container có một **writable layer** mỏng nằm trên các layer chỉ đọc của image. Ghi dữ liệu vào writable layer có ba nhược điểm:

| Nhược điểm | Hậu quả với MySQL |
|---|---|
| **Mất khi xóa container** (`docker rm`, `docker compose down`, compose tạo lại container) | Mất toàn bộ phòng, thành viên, chi tiêu |
| **Chậm**: sửa một file có sẵn trong image phải copy cả file lên writable layer trước (copy-up) | InnoDB ghi liên tục, hiệu năng giảm |
| **Gắn chặt với container**: không chia sẻ cho container khác, khó backup | Không nâng cấp được `mysql:8.0.46` lên bản mới mà giữ dữ liệu |

Giải pháp: **mount** một vùng lưu trữ nằm **ngoài** container vào một đường dẫn bên trong nó. Mọi thứ ghi vào đường dẫn đó đi thẳng ra ngoài, không qua writable layer.

### 1.2. Ba loại mount

| Loại | Dữ liệu nằm ở đâu | Ai quản lý | Dùng cho | Ví dụ |
|---|---|---|---|---|
| **Named volume** | Vùng do Docker cấp, trong VM Linux của Docker Desktop (`/var/lib/docker/volumes/<tên>/_data`) | Docker | Dữ liệu ứng dụng cần giữ lâu: database, file upload | `mysql-data:/var/lib/mysql` |
| **Bind mount** | Một thư mục **bạn chọn** trên máy host | Bạn | Đưa source code, file cấu hình từ host vào container | `./RoomSplit.Api:/src` (hot reload, xem [docker-dev-hot-reload.md](docker-dev-hot-reload.md)) |
| **tmpfs** | RAM, mất khi container dừng | Kernel | Dữ liệu tạm, nhạy cảm, không muốn ghi xuống đĩa | `--tmpfs /tmp` |

Ngoài ra có **anonymous volume** (volume ẩn danh): giống named volume nhưng tên là một chuỗi hash ngẫu nhiên. Xem [mục 1.5](#15-volume-ẩn-danh-và-chỉ-thị-volume-trong-dockerfile).

```
             Máy host (Windows)                         VM Linux của Docker Desktop
   ┌──────────────────────────────┐        ┌──────────────────────────────────────────┐
   │ D:\Workspace\RoomSplit\...   │        │ /var/lib/docker/volumes/                 │
   │        ▲                     │        │    roomsplit_mysql-data/_data  ▲         │
   └────────┼─────────────────────┘        └────────────────────────────────┼─────────┘
            │ bind mount                                         named volume│
   ┌────────┼──────────── container ─────────────────────────────────────────┼─────────┐
   │      /src                                                     /var/lib/mysql      │
   │  (writable layer: chỉ còn chứa những gì ghi NGOÀI các đường dẫn đã mount)         │
   └────────────────────────────────────────────────────────────────────────────────────┘
```

### 1.3. Named volume chi tiết

**Vòng đời độc lập với container.** Volume chỉ bị xóa khi bạn yêu cầu rõ ràng:

| Lệnh | Container | Named volume |
|---|---|---|
| `docker compose stop` | Dừng, vẫn còn | Còn |
| `docker compose down` | Xóa | **Còn** |
| `docker compose down -v` | Xóa | ⚠️ **Xóa** (mất dữ liệu) |
| `docker volume rm <tên>` | (phải xóa container dùng nó trước) | Xóa |
| `docker volume prune` | | Xóa volume ẩn danh không còn container nào dùng. Thêm `-a` thì xóa cả named volume không dùng |

**Tự đổ dữ liệu sẵn có (pre-populate).** Khi mount một named volume **còn rỗng** vào một thư mục mà image **đã có sẵn file**, Docker copy các file đó vào volume trước. Thí nghiệm 5: mount volume rỗng vào `/etc` của busybox, sau đó mount cùng volume vào container khác thì thấy đủ 11 file của `/etc` cộng file mới ghi. Hành vi này chỉ xảy ra **một lần**, lúc volume rỗng.

**Bind mount thì ngược lại: che mất nội dung của image.** Thí nghiệm 6: bind mount một thư mục chỉ có `host.txt` vào `/etc`, container chỉ còn thấy `host.txt` (cộng `hostname`, `hosts`, `resolv.conf` do Docker tự mount thêm). Các file gốc của image vẫn nằm trong layer, chỉ bị che đi. Đây là lý do [hot reload](docker-dev-hot-reload.md) phải che `bin/` và `obj/` bằng volume riêng.

**Truy cập trên Windows.** Đường dẫn `/var/lib/docker/volumes/...` nằm **bên trong VM** của Docker Desktop, không phải ổ `C:` hay `D:`. Muốn xem hoặc lấy file trong volume, cách đơn giản nhất là chạy một container tạm mount volume đó ([mục 1.8](#18-backup-và-restore)).

### 1.4. Cú pháp khai báo

**Trên dòng lệnh** có hai kiểu, kết quả như nhau:

```bash
# Kiểu ngắn -v / --volume:  <nguồn>:<đích>[:tùy chọn]
docker run -v mysql-data:/var/lib/mysql mysql:8.0.46        # nguồn là TÊN  → named volume
docker run -v /home/me/conf:/etc/mysql/conf.d:ro mysql:8.0.46 # nguồn là ĐƯỜNG DẪN → bind mount

# Kiểu dài --mount: dài hơn nhưng rõ ràng, sai thì báo lỗi thay vì tự tạo
docker run --mount type=volume,source=mysql-data,target=/var/lib/mysql mysql:8.0.46
docker run --mount type=bind,source=/home/me/conf,target=/etc/mysql/conf.d,readonly mysql:8.0.46
```

| Điểm khác | `-v` | `--mount` |
|---|---|---|
| Nguồn bind mount không tồn tại | **Tự tạo** thư mục rỗng (dễ che mất dữ liệu mà không biết) | **Báo lỗi** |
| Độ dễ đọc | Ngắn, phải nhớ thứ tự | Có tên từng trường |

Tùy chọn hay dùng: `:ro` (chỉ đọc). Thí nghiệm 9: ghi vào volume mount `:ro` báo `Read-only file system`.

**Trong compose**, cách Docker phân biệt: nguồn bắt đầu bằng `.`, `/` hoặc `~` là bind mount; nguồn là một tên thì là named volume, và **tên đó phải được khai báo** ở khối `volumes:` cấp cao nhất:

```yaml
services:
  mysql:
    volumes:
      - mysql-data:/var/lib/mysql          # named volume (khai báo bên dưới)
      - ./db/init:/docker-entrypoint-initdb.d:ro   # bind mount (ví dụ, RoomSplit chưa dùng)

volumes:
  mysql-data:                              # compose tạo với tên <project>_mysql-data
  # shared-data:
  #   external: true                       # volume tạo sẵn bên ngoài, compose không tạo/xóa
```

**Tên thật của volume** có tiền tố là tên project. Vì vậy cùng một file compose nhưng ba project khác nhau sẽ có ba volume tách biệt, đúng như [docker-multi-env.md](docker-multi-env.md):

| Project | Volume |
|---|---|
| `roomsplit` (dev) | `roomsplit_mysql-data` |
| `roomsplit-staging` | `roomsplit-staging_mysql-data` |
| `roomsplit-prod` | `roomsplit-prod_mysql-data` |

Compose gắn label (`com.docker.compose.project`, `com.docker.compose.volume`) vào volume để nhận ra volume của mình. Xem bằng `docker volume inspect roomsplit_mysql-data`.

### 1.5. Volume ẩn danh và chỉ thị `VOLUME` trong Dockerfile

Image có thể khai báo `VOLUME <đường dẫn>` trong Dockerfile. Image `mysql:8.0.46` có:

```bash
docker image inspect mysql:8.0.46 --format '{{json .Config.Volumes}}'
# {"/var/lib/mysql":{}}
```

Nghĩa là: nếu chạy MySQL **mà không mount gì** vào `/var/lib/mysql`, Docker vẫn tự tạo một **volume ẩn danh** cho đường dẫn đó (thí nghiệm 7). Mục đích: dữ liệu database không bao giờ rơi vào writable layer chậm chạp.

Nhược điểm của volume ẩn danh:
- Tên là hash, khó biết của ai. Trên máy này có hai volume như vậy (`14f0…`, `266f…`), tạo từ tháng 3 và tháng 8, là tàn dư của các lần thử nghiệm trước.
- Xóa container bằng `docker rm` **không** xóa volume ẩn danh, nên chúng tích tụ dần. Chỉ `docker run --rm` hoặc `docker rm -v` mới xóa kèm.
- Lần chạy sau tạo volume ẩn danh **mới**, không dùng lại cái cũ, nên trông như "mất dữ liệu".

Bài học: với dữ liệu quan trọng, **luôn đặt tên volume**, như RoomSplit làm với `mysql-data`.

### 1.6. Các lưu ý riêng của image MySQL

- **Biến khởi tạo chỉ chạy khi volume rỗng.** `MYSQL_DATABASE`, `MYSQL_USER`, `MYSQL_PASSWORD` và các script trong `/docker-entrypoint-initdb.d` chỉ có tác dụng ở lần khởi động đầu, khi `/var/lib/mysql` còn trống. Đổi `MYSQL_ROOT_PASSWORD` trong `.env` sau đó **không** đổi mật khẩu thật trong DB. Đã gặp trong [docker-compose.md §2](docker-compose.md#2-hiện-trạng).
- **Volume gắn với phiên bản MySQL.** Dữ liệu tạo bởi MySQL 8.0 không chắc mở được bằng image cũ hơn. Nâng cấp (vd 8.0 → 8.4) thì backup trước.
- **Không chia sẻ một volume MySQL cho hai container MySQL chạy cùng lúc.** InnoDB khóa file, container thứ hai sẽ lỗi hoặc làm hỏng dữ liệu.

### 1.7. Hiệu năng và quyền file trên Windows

| Vấn đề | Giải thích | Áp dụng |
|---|---|---|
| Bind mount từ ổ `D:\` chậm | File nằm trên filesystem Windows, mỗi lần đọc/ghi phải đi qua lớp chia sẻ file giữa Windows và VM | Database luôn dùng named volume (nằm sẵn trong VM, nhanh). Bind mount chỉ dùng cho source code |
| Theo dõi thay đổi file (file watching) qua bind mount kém tin cậy | Sự kiện thay đổi file của Windows không truyền đầy đủ vào Linux | Lý do [hot reload](docker-dev-hot-reload.md) phải cân nhắc polling hoặc Compose Watch |
| Quyền sở hữu file (UID) | Container API chạy bằng user `app` (UID 1654), không phải root. Volume mới tạo thường thuộc root, user `app` không ghi được | Hiện API không ghi file nên chưa gặp. Khi cần (vd thư mục upload), phải tạo sẵn thư mục với đúng chủ sở hữu trong Dockerfile trước khi mount volume |

### 1.8. Backup và restore

**Cách tổng quát cho mọi volume**: chạy container tạm, mount volume và một thư mục host, rồi nén:

```bash
# Backup volume → file .tar.gz trên host (chạy khi MySQL ĐÃ DỪNG, tránh dữ liệu đang ghi dở)
docker run --rm -v roomsplit_mysql-data:/data:ro -v "$PWD":/backup busybox:1.37 \
  tar czf /backup/mysql-data.tar.gz -C /data .

# Restore vào một volume (rỗng)
docker run --rm -v roomsplit_mysql-data:/data -v "$PWD":/backup busybox:1.37 \
  tar xzf /backup/mysql-data.tar.gz -C /data
```

**Với MySQL, nên dùng `mysqldump`** vì nó cho ra file SQL nhất quán khi MySQL **vẫn đang chạy**, không phụ thuộc phiên bản MySQL:

```bash
docker compose exec mysql sh -c 'mysqldump -uroot -p"$MYSQL_ROOT_PASSWORD" --single-transaction roomsplit' > roomsplit.sql
```

---

## 2. Network: container nói chuyện với nhau thế nào

### 2.1. Mỗi container có network stack riêng

Linux cho mỗi container một **network namespace**: card mạng ảo, IP, bảng định tuyến, cổng và `localhost` **riêng**. Hệ quả:

- `localhost` trong container `api` là **chính container `api`**. MySQL không chạy ở đó nên `Server=localhost` thất bại.
- Hai container cùng lắng nghe cổng 8080 không xung đột, vì mỗi cái có "máy" riêng.
- Muốn nói chuyện với nhau, các container phải được nối vào **cùng một network**.

### 2.2. Các network driver

| Driver | Ý nghĩa | Khi nào dùng |
|---|---|---|
| **bridge** | Một switch ảo trong máy. Container cắm vào, có IP nội bộ, ra Internet qua NAT | Mặc định, dùng cho gần như mọi trường hợp một máy. RoomSplit dùng loại này |
| **host** | Container dùng chung network stack với host, không có cách ly. Không cần `-p` | Cần hiệu năng mạng tối đa trên Linux. Trên Docker Desktop, "host" là VM chứ không phải Windows |
| **none** | Chỉ có `lo`, không có mạng | Job xử lý dữ liệu không được phép ra mạng. Container `my_container` cũ trên máy này dùng `none` |
| **overlay** | Nối container trên **nhiều máy** | Docker Swarm. Network `docker_gwbridge` trên máy là dấu vết của Swarm |
| **macvlan / ipvlan** | Container có IP thật trong mạng LAN | Thiết bị, hệ thống cũ cần IP riêng. Hiếm khi cần |

`docker network ls` trên máy này luôn có sẵn ba network: `bridge`, `host`, `none`.

### 2.3. Bridge mặc định và bridge tự tạo: khác nhau ở DNS

Đây là điểm quan trọng nhất của phần network.

| | Bridge mặc định (`bridge`) | Bridge tự tạo (`docker network create`, hoặc network compose tạo) |
|---|---|---|
| Container vào khi nào | Khi `docker run` không có `--network` | Khi chỉ định `--network <tên>`, hoặc mọi service trong compose |
| **Gọi nhau bằng tên** | ❌ Không. Thí nghiệm 1: `ping lab-a` → `bad address` | ✅ Có. Thí nghiệm 2: `ping lab-b` → trả lời từ `172.23.0.2` |
| Cách ly | Mọi container "không chỉ định" đều chung một mạng | Chỉ container trong cùng network thấy nhau |
| Gắn/gỡ khi đang chạy | Không | `docker network connect/disconnect` |

**DNS nội bộ hoạt động thế nào?** Trong network tự tạo, Docker ghi `nameserver 127.0.0.11` vào `/etc/resolv.conf` của container (thí nghiệm 2). Đó là **DNS server nhúng** của Docker:
- Tên là tên container, tên service hoặc alias trong network đó → trả về IP container.
- Tên khác (vd `nuget.org`) → chuyển tiếp ra DNS của host.

Vì IP container **thay đổi** mỗi lần tạo lại, luôn gọi nhau **bằng tên**, không bao giờ hard-code IP.

> 🔎 So sánh đầy đủ (DNS, cách ly, alias, IP tĩnh, cấu hình, vòng đời, network compose tạo vs tạo tay) ở [mục 2.9](#29-đi-sâu-network-docker-tạo-sẵn-và-network-do-user-tạo).

### 2.4. Bên trong một bridge network

```
                   Internet / mạng LAN
                          ▲
                          │  NAT (đổi IP nguồn 172.20.x.x → IP của host)
   ┌──────────────────────┼────────────── VM Linux (Docker host) ─────────────────┐
   │                      │                                                        │
   │   ┌──────────────────┴─────────────── bridge "roomsplit_default" ─────────┐   │
   │   │  subnet 172.20.0.0/16, gateway 172.20.0.1 (DNS nhúng 127.0.0.11)      │   │
   │   └──────┬──────────────────────────────────────────┬──────────────────────┘   │
   │          │ veth (dây mạng ảo)                       │ veth                     │
   │   ┌──────┴───────────────┐                  ┌───────┴──────────────┐           │
   │   │ roomsplit-mysql      │                  │ roomsplit-api        │           │
   │   │ eth0 172.20.0.2      │ ◄── mysql:3306 ──│ eth0 172.20.0.3      │           │
   │   │ nghe :3306           │                  │ nghe :8080           │           │
   │   └──────▲───────────────┘                  └───────▲──────────────┘           │
   └──────────┼──────────────────────────────────────────┼─────────────────────────┘
              │ -p 3307:3306                             │ -p 8080:8080
        localhost:3307 (dotnet run, DBeaver)       localhost:8080/swagger (trình duyệt)
```

- Mỗi container nối vào bridge bằng một cặp **veth** (như một sợi dây mạng ảo, một đầu trong container là `eth0`, đầu kia cắm vào switch ảo).
- Subnet do Docker tự cấp: bridge mặc định dùng `172.17.0.0/16`, network của compose RoomSplit được cấp `172.20.0.0/16`.
- Ra Internet: Docker dùng NAT (iptables) để đổi IP nguồn thành IP của host.

### 2.5. Publish port: mở cửa từ host vào container

Mặc định, **không gì từ bên ngoài vào được** container. `-p` (hay `ports:` trong compose) tạo một quy tắc chuyển tiếp:

```
-p [IP_host:]cổng_host:cổng_container
-p 3307:3306            # host:3307  → container:3306, nghe trên MỌI card mạng của host
-p 127.0.0.1:3307:3306  # chỉ máy này truy cập được
```

| Hiểu lầm hay gặp | Thực tế |
|---|---|
| `EXPOSE 8080` trong Dockerfile là mở cổng | `EXPOSE` chỉ là **tài liệu**. Không có `-p` thì host vẫn không vào được |
| Container khác cần `-p` mới gọi được | **Không.** Container cùng network gọi thẳng `mysql:3306`. `-p` chỉ dành cho truy cập **từ host** |
| `-p 3307:3306` thì container khác phải dùng 3307 | **Không.** 3307 là cổng của host. Trong network vẫn là **3306**. Đây là lý do connection string trong compose là `Server=mysql;Port=3306`, còn `dotnet run` trên host dùng `Port=3307` |
| `-p 3307:3306` chỉ máy mình truy cập được | Không có IP phía trước thì nghe trên `0.0.0.0`, tức **máy khác trong mạng LAN** có thể kết nối (nếu firewall Windows cho phép) |

### 2.6. Container gọi ra máy host

Ngược với 2.5: khi container cần gọi một dịch vụ chạy trên Windows (vd MySQL cũ `MySQL80` trên cổng 3306), dùng hostname đặc biệt **`host.docker.internal`**, Docker Desktop tự phân giải thành IP của host. `localhost` không dùng được, như đã giải thích ở 2.1.

```
Server=host.docker.internal;Port=3306;...   # container → MySQL80 cài trên Windows
```

### 2.7. Network trong docker compose

Compose tự làm các việc sau, không cần khai báo:

1. Tạo network `<project>_default` (bridge tự tạo), vd `roomsplit_default`.
2. Cho mọi service tham gia network đó.
3. Đăng ký DNS cho mỗi container với nhiều tên. Thí nghiệm 10: container MySQL trả lời được bằng cả **tên service** `mysql` lẫn **tên container** `roomsplit-mysql`. Luôn dùng **tên service**, vì tên container có thể khác giữa các môi trường (staging tên là `roomsplit-staging-mysql-1`).
4. `docker compose down` xóa network. `up` tạo lại.

**Cách ly giữa các project.** Mỗi project một network riêng: `roomsplit_default`, `roomsplit-staging_default`, `roomsplit-prod_default`. API staging gọi `mysql` thì chỉ tìm thấy MySQL **của staging**, không bao giờ nhầm sang production, dù cả hai service cùng tên `mysql`. Thí nghiệm 3 chứng minh container ở network khác ping IP không được (100% packet loss).

**Nhiều network trong một project** (RoomSplit chưa cần, ví dụ để hiểu):

```yaml
services:
  proxy:            # reverse proxy (vd nginx), nhận request từ ngoài
    networks: [frontend]
    ports: ["80:80"]
  api:
    networks: [frontend, backend]   # cầu nối, vào cả hai network
  mysql:
    networks: [backend]             # proxy KHÔNG thể gọi thẳng mysql

networks:
  frontend:
  backend:
    internal: true                  # network không có đường ra Internet
```

Một container vào hai network thì có hai card mạng (thí nghiệm 4: `eth0` 172.17.0.2 và `eth1` 172.23.0.3) và gọi được container ở cả hai bên.

### 2.8. Không dùng compose: các container riêng lẻ giao tiếp thế nào?

**Được.** Compose không có "phép thuật" riêng: nó chỉ gọi Docker API giống hệt `docker network create` + `docker run --network`. Khi chạy tay, bạn phải tự làm những việc compose làm hộ.

Có 4 cách, đều đã chạy thử với image thật `roomsplit-api:dev` + `mysql:8.0.46` (thí nghiệm 11–15):

| Cách | API kết nối MySQL bằng | Khuyến nghị |
|---|---|---|
| **1. User-defined network** | `Server=<tên container MySQL>;Port=3306` | ✅ **Cách chuẩn**, tương đương compose |
| 2. Publish port MySQL, đi vòng qua host | `Server=host.docker.internal;Port=<cổng host>` | ⚠️ Chạy được, nhưng MySQL bị lộ ra host, đi đường vòng |
| 3. `--link` trên bridge mặc định | `Server=<alias>;Port=3306` | ❌ **Đã lỗi thời (deprecated)**, Docker in cảnh báo |
| 4. `--network container:<tên>` | `Server=127.0.0.1;Port=3306` | Trường hợp đặc biệt (sidecar), không dùng cho app thường |

#### Cách 1: user-defined network (nên dùng)

Đây chính là những gì compose làm cho RoomSplit, viết lại bằng tay:

```bash
# 1. Tạo network (compose tự làm: roomsplit_default) và volume (roomsplit_mysql-data)
docker network create roomsplit-net
docker volume create roomsplit-db

# 2. MySQL: vào network, KHÔNG cần -p (API gọi qua network nội bộ)
docker run -d --name mysql --network roomsplit-net \
  -v roomsplit-db:/var/lib/mysql \
  -e MYSQL_ROOT_PASSWORD=secret -e MYSQL_DATABASE=roomsplit \
  --health-cmd='mysqladmin ping -h localhost -psecret' --health-interval=5s \
  mysql:8.0.46

# 3. Tự chờ MySQL healthy (compose làm bằng depends_on: condition: service_healthy)
until [ "$(docker inspect -f '{{.State.Health.Status}}' mysql)" = healthy ]; do sleep 2; done

# 4. API: cùng network, gọi MySQL bằng TÊN CONTAINER; -p để trình duyệt trên host vào được
docker run -d --name api --network roomsplit-net -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e "ConnectionStrings__DefaultConnection=Server=mysql;Port=3306;Database=roomsplit;User=root;Password=secret;" \
  roomsplit-api:dev
```

Kết quả thật: `GET /health/ready` → `Healthy (HTTP 200)`. Trong container API, `getent hosts lab-mysql` → `172.23.0.2`.

**Quên `--network` thì sao?** Container API rơi vào bridge mặc định, không phân giải được tên `mysql`: `/health/ready` trả **HTTP 503** (thí nghiệm 12). Sửa ngay khi đang chạy bằng `docker network connect roomsplit-net api`, không cần tạo lại container: lập tức `HTTP 200`.

So sánh việc phải tự làm:

| Việc | Compose | Chạy tay |
|---|---|---|
| Tạo network, volume | Tự động, có tiền tố project | `docker network create`, `docker volume create` |
| DNS theo tên | Tên service (`mysql`) | Tên container (`--name`) hoặc `--network-alias` |
| Thứ tự khởi động, chờ healthy | `depends_on` + `condition` | Tự viết vòng lặp chờ |
| Cấu hình | Lưu trong file YAML, commit vào git | Nằm trong lệnh dài, dễ gõ sai, khó chia sẻ |
| Dọn dẹp | `docker compose down` | `docker rm -f`, `docker network rm` từng cái |

#### Cách 2: publish port, đi vòng qua host

```bash
docker run -d --name mysql -p 13306:3306 ... mysql:8.0.46
docker run -d --name api -p 8080:8080 \
  -e "ConnectionStrings__DefaultConnection=Server=host.docker.internal;Port=13306;..." roomsplit-api:dev
```

Đường đi: API → host (`host.docker.internal`, trên máy này là `192.168.65.254`) → cổng 13306 → MySQL. Chạy được (`HTTP 200`), nhưng:
- MySQL **bắt buộc** phải publish port, tức máy khác trong LAN có thể thấy.
- Phụ thuộc cổng host, đổi cổng là phải sửa connection string.
- `host.docker.internal` là tính năng của Docker Desktop. Trên Linux thuần phải thêm `--add-host=host.docker.internal:host-gateway`.

Chỉ hợp lý khi một bên **không** chạy trong Docker, vd `dotnet run` trên host gọi MySQL trong container (cách dev RoomSplit đang làm với cổng 3307), hoặc container gọi `MySQL80` cài trên Windows.

#### Cách 3: `--link` (lỗi thời)

```bash
docker run -d --name api --link mysql:db ... # rồi dùng Server=db
```

Docker ghi một dòng `172.17.0.2  db ...` vào `/etc/hosts` của container API (thí nghiệm 14). Nhược điểm: đây là ánh xạ **tĩnh**, MySQL tạo lại đổi IP là hỏng. Ngoài ra chỉ có một chiều, và Docker in cảnh báo *"Links on the default bridge network are deprecated and will be removed"*. Gặp trong tài liệu cũ thì biết, đừng dùng.

#### Cách 4: dùng chung network namespace

```bash
docker run -d --name api --network container:mysql ... # Server=127.0.0.1
```

Container API **không có mạng riêng** mà mượn mạng của container MySQL: cùng IP, cùng `localhost`. Vì vậy `127.0.0.1:3306` trỏ đúng MySQL (thí nghiệm 15: `Healthy`). Hệ quả:
- Không được dùng `-p` ở container mượn: `conflicting options: port publishing and the container type network mode`. Port phải publish ở container "chủ".
- Hai container không được nghe trùng cổng.

Dùng cho **sidecar**: container phụ trợ cần nhìn thấy đúng mạng của container chính, vd công cụ debug mạng (`docker run --rm -it --network container:roomsplit-api nicolaka/netshoot`), proxy, hoặc thu thập log. Kubernetes Pod hoạt động theo đúng nguyên lý này.

### 2.9. Đi sâu: network Docker tạo sẵn và network do user tạo

Kiểm tra thực tế ngày 2026-10-22 (thí nghiệm 16–26 ở [mục 3](#3-kết-quả-thí-nghiệm)).

#### 2.9.1. Network từ đâu ra?

Từ góc nhìn của Docker chỉ có **hai loại**: network **tạo sẵn** (pre-defined) và network **do user tạo** (user-defined). Network mà compose tạo cũng là user-defined, chỉ khác là có gắn thêm label.

| Nguồn gốc | Ví dụ trên máy này | Ai tạo, khi nào | Xoá được không |
|---|---|---|---|
| **Tạo sẵn** (pre-defined) | `bridge`, `host`, `none` | Docker daemon, ngay khi khởi động | ❌ `bridge is a pre-defined network and cannot be removed` (thí nghiệm 21) |
| **User tạo bằng tay** | `custom_bridge`, `frontend`, `backend` (thử nghiệm cũ) | Bạn, bằng `docker network create` | ✅ Khi không còn container nào dùng |
| **User-defined do compose tạo** | `roomsplit_default`, `roomsplit-staging_default`, `roomsplit-prod_default` | `docker compose up`, tên `<project>_<network>` | ✅ `docker compose down` tự xoá |
| Do tính năng khác tạo | `docker_gwbridge` | Docker Swarm (`docker swarm init`) | Xoá khi rời Swarm |

> "Network mặc định" có hai nghĩa dễ nhầm: (1) network `bridge` mà `docker run` dùng khi không có `--network` (đây là network **tạo sẵn**), và (2) network `default` của một project compose (`roomsplit_default`). Network (2) là **user-defined**, nên có đủ mọi ưu điểm ở bảng dưới.

#### 2.9.2. Bảng so sánh: bridge tạo sẵn và bridge user-defined

| Tiêu chí | Bridge tạo sẵn (`bridge`) | Bridge user-defined | Thí nghiệm |
|---|---|---|---|
| DNS theo tên container | ❌ `ping: bad address 'lab-b'` | ✅ Tên container, tên service, alias | 1, 2, 17 |
| DNS server trong `/etc/resolv.conf` | DNS của host được **chép vào** (`nameserver 192.168.65.7`, DNS của VM Docker Desktop) | DNS nhúng `nameserver 127.0.0.11` + `options ndots:0` | 16 |
| Cách ly | Mọi container chạy không có `--network` đều chung một mạng và **ping nhau được bằng IP** (ICC bật) | Chỉ container trong cùng network thấy nhau | 17, 18 |
| Alias (`--network-alias`) | ❌ `network-scoped aliases are only supported for user-defined networks` | ✅ | 19 |
| IP tĩnh (`--ip`) | ❌ `user-specified IP address is supported on user-defined networks only` | ✅ (IP phải nằm trong subnet của network) | 20 |
| Cấu hình subnet, gateway, MTU… | Toàn cục, sửa trong `daemon.json` (`bip`, `mtu`) rồi **khởi động lại Docker**, ảnh hưởng mọi container | Riêng từng network: `docker network create --subnet 10.10.0.0/24 --gateway ... --opt ...` | 20 |
| `internal` (không ra Internet) | ❌ | ✅ `--internal` | 23 |
| Tên bridge Linux trong VM | `docker0` | `br-<12 ký tự đầu của network ID>`, vd `br-3b9c4242bd0c` | 22 |
| Subnet | Cố định `172.17.0.0/16` | Lấy lần lượt từ pool: `roomsplit_default` 172.20, staging 172.21, prod 172.22, `lab-net` 172.25 | 16 |
| Gắn/gỡ khi container đang chạy | ✅ Được, trên Docker 29.7 (tài liệu cũ nói phải tạo lại container) | ✅ `docker network connect/disconnect` | 22 |
| Xoá | ❌ Không xoá được | ✅ | 21 |
| `--link` | Dùng được nhưng **deprecated** | Không cần (đã có DNS) | 14 |

#### 2.9.3. Giải thích từng khác biệt

**a. DNS: khác biệt lớn nhất.** Hai loại network cấu hình `resolv.conf` khác nhau:

```
# Container trên bridge tạo sẵn          # Container trên network user-defined
nameserver 192.168.65.7                  nameserver 127.0.0.11
                                         options ndots:0
```

- Bridge tạo sẵn: Docker chỉ **chép DNS của host** vào container. DNS của host không biết `lab-b` là gì, nên chỉ phân giải được tên Internet.
- User-defined: mọi truy vấn đi qua **DNS nhúng** `127.0.0.11` do Docker chạy. Tên của container trong cùng network được trả về IP nội bộ, còn tên khác được chuyển tiếp ra DNS của host.
- `/etc/hosts` ở **cả hai** loại chỉ có chính container đó (`172.17.0.2 2d70c54cdbef`). Như vậy tên container khác được phân giải qua DNS, không phải qua file hosts (trừ `--link`, cách cũ ghi thẳng vào `/etc/hosts`).

**b. Cách ly: bridge tạo sẵn là "phòng chung".** Mọi container chạy bằng `docker run` không có `--network` đều rơi vào cùng network `bridge`, và Docker bật ICC (inter-container communication, `enable_icc: true`). Ví dụ: bạn chạy thử một image lạ bằng `docker run` (không `--network`), trong khi MySQL cũng đang ở `bridge` → image lạ đó kết nối được thẳng tới MySQL theo IP. Với network user-defined, container ở network khác **không** tới được (thí nghiệm 18: 100% packet loss).

**c. Alias và IP tĩnh** chỉ có trên network user-defined, vì cả hai đều gắn với **một network cụ thể** (một container có thể mang alias `db` ở network này nhưng không ở network khác). Bridge tạo sẵn không có cơ chế quản lý tên/IP theo network như vậy.
- IP tĩnh chỉ nên dùng khi thật cần (vd firewall chỉ cho phép một IP). Ứng dụng thường thì gọi bằng tên.

**d. Cấu hình.** Muốn đổi subnet của `bridge` (vd vì trùng dải 172.17.x của VPN công ty) phải sửa `daemon.json` (Docker Desktop: Settings → Docker Engine) rồi khởi động lại Docker, ảnh hưởng **mọi** container. Network user-defined thì cấu hình riêng lúc tạo:

```bash
docker network create --subnet 10.10.0.0/24 --gateway 10.10.0.1 lab-fixed
docker run --rm --network lab-fixed --ip 10.10.0.50 busybox:1.37 ip -4 addr show eth0
# inet 10.10.0.50/24 ...   (thí nghiệm 20)
```

Compose tương đương:

```yaml
networks:
  default:
    ipam:
      config:
        - subnet: 10.10.0.0/24
```

**e. Hết dải IP.** Mỗi network user-defined lấy một subnet từ **pool mặc định** của Docker: các dải `172.17–172.31.x.x/16`, sau đó là `192.168.x.x/20`, tổng cộng khoảng 30 network. Tạo quá nhiều network (mỗi project compose ít nhất một) sẽ gặp lỗi `could not find an available, non-overlapping IPv4 address pool`. Cách xử lý: `docker network prune` (xoá network không dùng), hoặc khai báo `default-address-pools` với subnet nhỏ hơn (vd `/24`) trong `daemon.json`.

**f. Bridge Linux bên dưới.** Mỗi bridge network là một bridge Linux thật trong VM của Docker Desktop: `docker0` cho `bridge`, `br-<id>` cho network user-defined. Thí nghiệm 22: VM có `docker0` và 8 bridge `br-…`, khớp với 8 network bridge user-defined trên máy. Dùng tên này khi debug bằng `ip link`, `tcpdump -i br-…`.

#### 2.9.4. Network compose tạo và network tạo tay

Cả hai đều là user-defined, khác nhau ở **ai quản lý vòng đời**. Compose đánh dấu network của mình bằng label:

```json
"com.docker.compose.project": "roomsplit",
"com.docker.compose.network": "default",
"com.docker.compose.version": "5.4.0",
"com.docker.compose.config-hash": "812fa2e6…"
```

| Tình huống | Hành vi | Thí nghiệm |
|---|---|---|
| Network chưa có | `up` tạo `<project>_<network>` có label | 25 |
| `down` | Chỉ xoá network **có label của project này** | 25 |
| Đã có network trùng tên nhưng **tạo tay** (không label) | Compose vẫn dùng, nhưng cảnh báo `a network with name labproj_default exists but was not created by compose. Set external: true to use an existing network`. `down` **không** xoá network đó | 24 |
| `external: true` | Compose chỉ dùng, không tạo, không xoá. Network chưa có thì `up` báo lỗi | 26 |

**Khi nào tạo network bằng tay rồi khai báo `external`?** Khi network phải **sống lâu hơn** một project hoặc **dùng chung** giữa nhiều project. Ví dụ: một reverse proxy (Traefik/nginx) đứng trước cả staging và prod:

```bash
docker network create proxy-net
```

```yaml
# docker-compose.prod.yml (ví dụ, RoomSplit chưa làm)
services:
  api:
    networks: [default, proxy]    # default: nói chuyện với mysql; proxy: proxy gọi vào api
networks:
  proxy:
    name: proxy-net               # tên thật, không thêm tiền tố project
    external: true
```

Thí nghiệm 26: container `web` của project `labproj` vào network `lab-net` tạo tay, và container `lab-c` (không thuộc compose) ping được `web` bằng tên service. Sau `down`, `lab-net` vẫn còn.

#### 2.9.5. Áp dụng vào RoomSplit

| Câu hỏi | Trả lời |
|---|---|
| RoomSplit đang dùng loại nào? | User-defined do compose tạo (`roomsplit_default`, …). Đúng thực hành tốt, không cần đổi |
| Có nên chạy container RoomSplit trên `bridge` tạo sẵn? | Không: mất DNS theo tên, nên `Server=mysql` không chạy được, và bị chung mạng với mọi container khác |
| Khi nào cần network tạo tay + `external`? | Khi có reverse proxy dùng chung cho staging và prod (bước P3, chưa làm) |
| Khi nào cần cố định subnet? | Khi dải `172.x` trùng với VPN hoặc LAN công ty (triệu chứng: bật Docker thì mất kết nối tới máy nội bộ). Hiện chưa gặp |

Tóm lại: **bridge tạo sẵn chỉ để chạy thử nhanh một container đơn lẻ.** Hễ có hơn một container cần nói chuyện với nhau thì dùng network user-defined (compose làm sẵn việc này).

---

## 3. Kết quả thí nghiệm

Chạy ngày 2026-10-21 với container và network tạm (`lab-*`), đã xóa sau khi xong.

| # | Thí nghiệm | Kết quả |
|---|---|---|
| 1 | Hai container trên bridge mặc định, `ping lab-a` | ❌ `ping: bad address 'lab-a'`: không có DNS theo tên |
| 2 | `docker network create lab-net`, container `lab-b` với alias `db` | ✅ `/etc/resolv.conf` có `nameserver 127.0.0.11`. `lab-b` và `db` đều phân giải ra `172.23.0.2`, ping được |
| 3 | Container trên bridge mặc định ping IP của `lab-b` (ở `lab-net`) | ❌ 100% packet loss: hai network cách ly |
| 4 | `docker network connect lab-net lab-a` | ✅ `lab-a` có thêm `eth1 172.23.0.3`, ping `lab-b` bằng tên được |
| 5 | Mount named volume rỗng vào `/etc` của busybox | ✅ Volume được đổ sẵn 11 file từ image. File ghi thêm còn nguyên khi mount vào container khác |
| 6 | Bind mount thư mục chỉ có `host.txt` vào `/etc` | Container chỉ thấy `host.txt` (+ `hostname`, `hosts`, `resolv.conf` do Docker mount): nội dung image bị che |
| 7 | `docker run mysql:8.0.46` không có `-v` | Docker tự tạo **volume ẩn danh** cho `/var/lib/mysql`. `docker rm -f` container thì volume **vẫn còn** |
| 8 | `docker run --rm mysql:8.0.46 ...` | Volume ẩn danh bị xóa cùng container |
| 9 | Mount volume với `:ro`, thử ghi | ❌ `Read-only file system`. `Mountpoint` = `/var/lib/docker/volumes/lab-vol/_data` (trong VM) |
| 10 | Inspect network compose `roomsplit_default` | Subnet `172.20.0.0/16`. Container MySQL có DNS names `roomsplit-mysql`, `mysql` và ID container |
| 11 | Không compose: `docker network create` + MySQL + API cùng network, `Server=lab-mysql` | ✅ MySQL healthy sau ~12 s, `/health/ready` → `Healthy (HTTP 200)` |
| 12 | Như 11 nhưng API không có `--network` | ❌ `HTTP 503`. Sau `docker network connect lab-net lab-api2` → `HTTP 200` |
| 13 | MySQL `-p 13306:3306`, API `Server=host.docker.internal;Port=13306` | ✅ `HTTP 200`. `host.docker.internal` = `192.168.65.254` |
| 14 | `--link lab-mysql-pub:db`, `Server=db` | ✅ `HTTP 200` kèm cảnh báo deprecated. `/etc/hosts` có `172.17.0.2 db` |
| 15 | `--network container:lab-mysql-pub`, `Server=127.0.0.1` | ✅ `Healthy`. Thêm `-p` → lỗi `conflicting options` |

Thí nghiệm 16–26 chạy ngày 2026-10-22 (cho [mục 2.9](#29-đi-sâu-network-docker-tạo-sẵn-và-network-do-user-tạo)), đã dọn sạch sau khi xong:

| # | Thí nghiệm | Kết quả |
|---|---|---|
| 16 | `docker network inspect` `bridge`, `roomsplit_default`, `lab-net` mới tạo | `bridge`: option `default_bridge: true`, `enable_icc: true`, `bridge.name: docker0`, subnet `172.17.0.0/16`, không label. Network compose: label `com.docker.compose.*`, subnet 172.20/21/22. `lab-net`: 172.25, không label. `resolv.conf`: bridge tạo sẵn `nameserver 192.168.65.7`; user-defined `nameserver 127.0.0.11`, `options ndots:0` |
| 17 | Hai container trên `bridge` tạo sẵn: ping bằng IP, rồi bằng tên | IP: ✅ 0% loss. Tên: ❌ `bad address 'lab-b'`. `/etc/hosts` chỉ có chính container |
| 18 | Container trên `bridge` ping IP của container ở `lab-net` | ❌ 100% packet loss |
| 19 | `docker run --network-alias db` (trên `bridge`) | ❌ `network-scoped aliases are only supported for user-defined networks` |
| 20 | `--ip 172.17.0.50` trên `bridge`; `--ip` trên `lab-net` và trên `lab-fixed` (`--subnet 10.10.0.0/24`) | `bridge`: ❌ `user-specified IP address is supported on user-defined networks only`. `lab-net` với IP trong subnet: ✅, ngoài subnet: ❌. `lab-fixed`: ✅ `inet 10.10.0.50/24` |
| 21 | `docker network rm bridge` | ❌ `bridge is a pre-defined network and cannot be removed` |
| 22 | `docker network disconnect bridge` / `connect bridge` container đang chạy; `connect --alias x bridge`; `ip link` trong VM (`--net host`) | Gỡ/gắn: ✅ (Docker 29.7). Kèm alias: ❌. VM có `docker0` + 8 bridge `br-…`, có `br-3b9c4242bd0c` khớp ID `lab-net` |
| 23 | Container trên network `--internal` gọi `http://example.com`; so sánh với `lab-net` | Internal: ❌ `bad address`, bảng định tuyến không có default route. `lab-net`: ✅ ra Internet |
| 24 | Tạo tay `labproj_default`, rồi `docker compose -p labproj up` | ⚠️ Cảnh báo `exists but was not created by compose`, vẫn chạy. `down` không xoá network (label rỗng) |
| 25 | Để compose tự tạo `labproj_default`, rồi `down` | `up`: `Network labproj_default Created`, có label. `down`: `Removed` |
| 26 | Compose dùng `lab-net` với `external: true` | Container `lab-c` (ngoài compose) ping `web` bằng tên: ✅. `down` không xoá `lab-net` |

Hiện trạng volume trên máy (`docker system df -v`):

| Volume | Dung lượng | Ghi chú |
|---|---|---|
| `roomsplit_mysql-data` | 197.9 MB | DB dev |
| `roomsplit-staging_mysql-data` | 198 MB | DB staging |
| `roomsplit-prod_mysql-data` | 198 MB | DB production (mô phỏng) |
| `14f0…`, `266f…` | 0 B, 49 MB | Volume ẩn danh cũ, từ thử nghiệm trước. `266f…` đã bị xóa ngày 2026-10-22 bởi `docker volume prune -f` khi dọn thí nghiệm 11–15 (prune xóa **mọi** volume ẩn danh không container nào dùng, không chỉ volume vừa tạo) |
| `my_volume` | 12 B | Volume thử nghiệm cũ |

---

## 4. Áp dụng vào RoomSplit

### 4.1. Cấu hình hiện tại đã đúng những gì

| Cấu hình | Vì sao đúng |
|---|---|
| `mysql-data:/var/lib/mysql` là **named volume** | Dữ liệu sống sót qua `down`/`up` và lần compose tự tạo lại container (đã xảy ra, xem [docker-compose §7](docker-compose.md#7-kết-quả-triển-khai)) |
| API dùng `Server=mysql;Port=3306` | Gọi bằng tên service, cổng nội bộ, không phụ thuộc IP hay cổng host |
| Mỗi môi trường một project | Volume và network tự tách theo tiền tố project, không thể lẫn dữ liệu |
| `ports` của MySQL chỉ nằm trong `docker-compose.override.yml` (dev) | Staging/production (`docker-compose.prod.yml`) **không publish** cổng MySQL ra host. Chỉ API trong cùng network gọi được. Đây là thực hành bảo mật tốt |

### 4.2. Điểm có thể cải thiện (chưa làm, để cân nhắc)

| # | Đề xuất | Lợi ích | Đánh đổi |
|---|---|---|---|
| 1 | Đổi `"${MYSQL_PORT:-3306}:3306"` thành `"127.0.0.1:${MYSQL_PORT:-3306}:3306"` trong override | MySQL dev chỉ truy cập từ chính máy này, máy khác trong mạng LAN (vd Wi-Fi quán cà phê) không thấy | Không truy cập được DB dev từ máy khác, điều mà dev một mình gần như không cần |
| 2 | Dọn volume ẩn danh `14f0…` và `my_volume` cũ (`266f…` đã bị xóa, xem mục 3) | Danh sách gọn | Cần xác nhận không còn dùng. Volume `14f0…` đang gắn với container `floci` đã dừng |
| 3 | Network `backend` với `internal: true` cho MySQL ở production | MySQL không ra được Internet, giảm bề mặt tấn công | Chỉ có ý nghĩa khi có reverse proxy đứng trước API. Chưa cần ở MVP |

Đề xuất 1 thay đổi cấu hình compose, đề xuất 2 xóa dữ liệu, nên cần bạn quyết định trước khi làm.

---

## 5. Bảng lệnh tra cứu nhanh

### Volume

```bash
docker volume ls                                   # liệt kê
docker volume ls -f dangling=true                  # volume không container nào dùng
docker volume inspect roomsplit_mysql-data         # nơi lưu, label, ngày tạo
docker system df -v                                # dung lượng từng volume
docker run --rm -v roomsplit_mysql-data:/data:ro busybox:1.37 ls -la /data   # xem bên trong
docker volume rm <tên>                             # xóa một volume (không còn container dùng)
docker volume prune                                # xóa volume ẩn danh không dùng
docker compose down -v                             # ⚠️ xóa cả volume của project
```

### Network

```bash
docker network ls
docker network inspect roomsplit_default           # subnet, gateway, container nào đang trong đó
docker network inspect roomsplit_default \
  --format '{{range .Containers}}{{.Name}} {{.IPv4Address}}{{println}}{{end}}'
docker network create <tên>                        # bridge tự tạo (có DNS)
docker network connect <network> <container>       # gắn container đang chạy vào network
docker port roomsplit-mysql                        # các cổng đã publish
docker run --rm --network roomsplit_default busybox:1.37 nslookup mysql   # thử DNS nội bộ
docker run --rm --network roomsplit_default busybox:1.37 nc -zv mysql 3306 # thử cổng
```

### Tóm tắt một dòng

- **Volume**: container là thứ dùng xong bỏ, dữ liệu phải nằm trong **named volume**. Bind mount để đưa code vào, tmpfs cho dữ liệu tạm.
- **Network**: dùng **bridge tự tạo** (compose làm sẵn), gọi nhau bằng **tên service** và **cổng nội bộ**. `-p` chỉ để mở cửa cho host, và chỉ publish những gì thật sự cần.
