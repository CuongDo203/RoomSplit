# Docker volume và network: tìm hiểu sâu, áp dụng vào RoomSplit

Tài liệu học tập. Mọi kết quả ghi trong bài được kiểm tra thực tế trên máy dev (Docker Engine 29.7.2, Docker Desktop trên WSL2) ngày 2026-10-21, bằng container thử nghiệm `busybox:1.37` và `mysql:8.0.46`. Chi tiết ở [mục 3](#3-kết-quả-thí-nghiệm).

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

Hiện trạng volume trên máy (`docker system df -v`):

| Volume | Dung lượng | Ghi chú |
|---|---|---|
| `roomsplit_mysql-data` | 197.9 MB | DB dev |
| `roomsplit-staging_mysql-data` | 198 MB | DB staging |
| `roomsplit-prod_mysql-data` | 198 MB | DB production (mô phỏng) |
| `14f0…`, `266f…` | 0 B, 49 MB | Volume ẩn danh cũ, từ thử nghiệm trước, có thể dọn |
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
| 2 | Dọn volume ẩn danh và `my_volume` cũ (`docker volume rm 14f0… 266f… my_volume`) | Giải phóng ~49 MB, danh sách gọn | Cần xác nhận không còn dùng. Volume `14f0…` đang gắn với container `floci` đã dừng |
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
