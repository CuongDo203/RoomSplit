# Redis: kiến thức cơ bản

Tài liệu học tập. Bài này giải thích Redis là gì, các khái niệm nền tảng, và khi nào (nếu có) nên đưa vào dự án. Các lệnh trong bài là lệnh chuẩn của Redis; phần thử nghiệm ở [mục 8](#8-thử-nhanh-với-docker) chưa được chạy trên máy dev, khi chạy thử hãy đối chiếu với output thực tế.

Kiến thức nền: [docker-basics.md](docker-basics.md), [docker-compose.md](docker-compose.md).

> 🆕 **2026-10-24:** RoomSplit đã dùng Redis để cache danh sách member, xem [redis-members-cache.md](redis-members-cache.md) (thiết kế, kiểm tra, kết quả đo).

## Mục lục
1. [Redis là gì](#1-redis-là-gì)
2. [Vì sao Redis nhanh](#2-vì-sao-redis-nhanh)
3. [Các kiểu dữ liệu](#3-các-kiểu-dữ-liệu)
4. [Hết hạn (TTL) và thu hồi bộ nhớ (eviction)](#4-hết-hạn-ttl-và-thu-hồi-bộ-nhớ-eviction)
5. [Lưu bền (persistence): RDB và AOF](#5-lưu-bền-persistence-rdb-và-aof)
6. [Các mẫu sử dụng phổ biến](#6-các-mẫu-sử-dụng-phổ-biến)
7. [Transaction, pipeline, Lua, pub/sub](#7-transaction-pipeline-lua-pubsub)
8. [Thử nhanh với Docker](#8-thử-nhanh-với-docker)
9. [Dùng Redis trong ASP.NET Core](#9-dùng-redis-trong-aspnet-core)
10. [Replication, Sentinel, Cluster](#10-replication-sentinel-cluster)
11. [Bảo mật và vận hành](#11-bảo-mật-và-vận-hành)
12. [Redis có cần cho RoomSplit không?](#12-redis-có-cần-cho-roomsplit-không)
13. [Bảng lệnh tra cứu nhanh](#13-bảng-lệnh-tra-cứu-nhanh)

---

## 1. Redis là gì

**Redis** (REmote DIctionary Server) là một **kho dữ liệu key–value nằm trong RAM** (in-memory data store). Mỗi bản ghi có một **key** (chuỗi) và một **value**, nhưng value không chỉ là chuỗi mà có thể là list, set, hash, sorted set, stream…

So sánh với MySQL mà RoomSplit đang dùng:

| Tiêu chí | MySQL | Redis |
|---|---|---|
| Nơi chứa dữ liệu chính | Đĩa (InnoDB), RAM chỉ là cache | RAM; đĩa chỉ để khôi phục khi khởi động lại |
| Mô hình | Bảng, cột, quan hệ, khóa ngoại | Key → value có kiểu (string, hash, list…) |
| Truy vấn | SQL, JOIN, GROUP BY, index tùy ý | Lệnh theo key (`GET`, `HSET`, `ZADD`…), không có JOIN |
| Ràng buộc | CHECK, FK, UNIQUE, transaction ACID | Gần như không có; ứng dụng tự đảm bảo |
| Độ trễ điển hình | Vài ms | Dưới 1 ms |
| Dung lượng | Lớn hơn RAM rất nhiều | Bị giới hạn bởi RAM |
| Vai trò thường gặp | **Nguồn sự thật** (source of truth) | Cache, session, bộ đếm, hàng đợi, rate limit |

Ý chính: Redis **thường không thay thế** database quan hệ mà **đứng cạnh** nó, giữ những dữ liệu cần đọc/ghi cực nhanh hoặc dữ liệu tạm thời.

### 1.1. Giấy phép và các bản fork

- Đến Redis 7.2: giấy phép BSD (mã nguồn mở thoáng).
- Từ Redis 7.4 (2024): chuyển sang RSALv2/SSPL (không còn được coi là "open source" theo OSI). Cộng đồng Linux Foundation tách ra bản fork **Valkey** (BSD), tương thích giao thức và lệnh.
- Từ Redis 8 (2025): thêm lựa chọn giấy phép **AGPLv3**.

Với việc học và dùng nội bộ, khác biệt này gần như không ảnh hưởng: client, lệnh và khái niệm trong bài áp dụng cho cả Redis lẫn Valkey.

## 2. Vì sao Redis nhanh

1. **Dữ liệu ở RAM**: không phải đọc đĩa khi truy vấn.
2. **Cấu trúc dữ liệu đơn giản, độ phức tạp biết trước**: `GET`/`SET` là O(1), `ZADD` là O(log N). Tài liệu chính thức ghi độ phức tạp của từng lệnh.
3. **Xử lý lệnh trên một luồng chính** (single-threaded event loop): không tốn chi phí khóa (lock) giữa các luồng. Mỗi lệnh chạy **nguyên tử** (atomic): không lệnh nào chen vào giữa một lệnh khác. (Từ Redis 6, việc đọc/ghi socket có thể dùng nhiều luồng I/O, nhưng việc thực thi lệnh vẫn tuần tự.)
4. **Giao thức RESP gọn nhẹ** trên TCP (cổng mặc định **6379**).

Hệ quả của mục 3: **một lệnh chậm chặn tất cả** client khác. Ví dụ `KEYS *` trên DB có hàng triệu key sẽ làm Redis "đứng" vài giây. Trên production dùng `SCAN` thay cho `KEYS`.

## 3. Các kiểu dữ liệu

### 3.1. String

Kiểu cơ bản nhất, chứa chuỗi, số, hoặc dữ liệu nhị phân (JSON, ảnh nhỏ…), tối đa 512 MB.

```
SET room:1:name "Phòng 101"
GET room:1:name                 → "Phòng 101"

SET visits 0
INCR visits                     → 1   (tăng nguyên tử, an toàn khi nhiều client cùng gọi)
INCRBY visits 10                → 11

SET otp:0901234567 "482913" EX 300   # tự xóa sau 300 giây
SET lock:job "worker-1" NX EX 30     # chỉ set nếu key CHƯA tồn tại (dùng làm khóa)
```

### 3.2. Hash

Một key chứa nhiều cặp field–value, giống một object/một dòng của bảng.

```
HSET member:5 full_name "Bình" phone "0901234567" status "Active"
HGET member:5 full_name         → "Bình"
HGETALL member:5                → full_name, Bình, phone, ..., status, Active
HINCRBY room:1:stats expense_count 1
```

### 3.3. List

Danh sách có thứ tự theo thứ tự chèn (linked list). Thêm/bớt ở hai đầu là O(1).

```
LPUSH recent:room:1 "exp:42"    # thêm vào đầu
LTRIM recent:room:1 0 9         # chỉ giữ 10 phần tử mới nhất
LRANGE recent:room:1 0 -1       # lấy tất cả
RPUSH queue:emails "..."        # hàng đợi đơn giản: producer RPUSH
BLPOP queue:emails 0            # consumer chờ (block) đến khi có phần tử
```

### 3.4. Set

Tập hợp không thứ tự, không trùng lặp. Có phép giao, hợp, hiệu.

```
SADD room:1:members 5 6 7
SISMEMBER room:1:members 6      → 1
SMEMBERS room:1:members
SINTER room:1:members room:2:members   # thành viên chung
```

### 3.5. Sorted set (ZSET)

Giống set nhưng mỗi phần tử có một **score** (số thực); luôn được sắp theo score. Dùng cho bảng xếp hạng, hàng đợi theo thời gian.

```
ZADD room:1:balances 200000 "An" -100000 "Binh" -100000 "Chi"
ZRANGE room:1:balances 0 -1 WITHSCORES          # từ nhỏ đến lớn
ZREVRANGE room:1:balances 0 0 WITHSCORES        # người được nợ nhiều nhất
ZINCRBY room:1:balances 100000 "Binh"
```

### 3.6. Các kiểu khác (biết tên là đủ)

| Kiểu | Dùng cho |
|---|---|
| **Stream** | Log sự kiện chỉ thêm (append-only), consumer group; giống Kafka thu nhỏ |
| **Bitmap** | Đánh dấu bật/tắt theo vị trí bit (ví dụ: user nào đăng nhập ngày nào) |
| **HyperLogLog** | Đếm xấp xỉ số phần tử khác nhau với ~12 KB bộ nhớ |
| **Geospatial** | Lưu tọa độ, tìm điểm trong bán kính |
| **JSON, vector, time series…** | Tích hợp sẵn trong Redis 8 (trước đây là module Redis Stack) |

### 3.7. Đặt tên key

Redis không có "bảng"; quy ước dùng dấu `:` để phân cấp:

```
<ứng dụng>:<đối tượng>:<id>[:<thuộc tính>]
roomsplit:room:1:balances
roomsplit:categories:all
```

Thêm tiền tố ứng dụng (`roomsplit:`) giúp nhiều ứng dụng dùng chung một Redis mà không đụng key nhau.

## 4. Hết hạn (TTL) và thu hồi bộ nhớ (eviction)

### 4.1. TTL

Mỗi key có thể có thời gian sống; hết hạn thì Redis tự xóa.

```
SET session:abc "..." EX 1800   # đặt TTL khi set
EXPIRE room:1:balances 60       # đặt TTL cho key có sẵn
TTL room:1:balances             → 57   (-1 = không hết hạn, -2 = key không tồn tại)
PERSIST room:1:balances         # bỏ TTL
```

Lưu ý: ghi đè key bằng `SET` (không kèm `EX`/`KEEPTTL`) sẽ **xóa TTL cũ**.

### 4.2. Eviction

Khi RAM dùng chạm `maxmemory`, Redis xử lý theo `maxmemory-policy`:

| Policy | Hành vi | Hợp với |
|---|---|---|
| `noeviction` (mặc định) | Từ chối lệnh ghi, trả lỗi OOM | Redis dùng làm kho dữ liệu, không được mất key |
| `allkeys-lru` | Xóa key ít được dùng gần đây nhất, trong mọi key | **Cache thuần** (khuyến nghị phổ biến) |
| `volatile-lru` | Như trên nhưng chỉ trong các key có TTL | Vừa cache vừa lưu dữ liệu cố định |
| `allkeys-lfu` / `volatile-lfu` | Xóa key ít được dùng thường xuyên nhất | Cache có "điểm nóng" rõ rệt |
| `volatile-ttl` | Xóa key sắp hết hạn trước | Ít dùng |

## 5. Lưu bền (persistence): RDB và AOF

Dữ liệu ở RAM sẽ mất khi Redis tắt, trừ khi bật persistence:

| Cơ chế | Cách hoạt động | Ưu | Nhược |
|---|---|---|---|
| **RDB** (snapshot) | Định kỳ fork tiến trình con, ghi toàn bộ dữ liệu ra file `dump.rdb` | File gọn, khởi động lại nhanh, tiện backup | Mất dữ liệu từ lần snapshot gần nhất (có thể vài phút) |
| **AOF** (append-only file) | Ghi lại từng lệnh ghi vào file; khởi động thì chạy lại | Mất tối đa ~1 giây (`appendfsync everysec`) | File lớn hơn, cần rewrite định kỳ |
| **RDB + AOF** | Kết hợp | An toàn nhất | Tốn I/O hơn |
| **Tắt hết** | Chỉ ở RAM | Nhanh nhất | Mất sạch khi restart; chấp nhận được với cache thuần |

Ngay cả khi bật AOF, Redis **không** đảm bảo bền vững như transaction InnoDB của MySQL. Đó là lý do dữ liệu tiền bạc (chi tiêu, thanh toán) của RoomSplit phải nằm ở MySQL.

Khi chạy trong Docker, thư mục dữ liệu là `/data`: mount một named volume vào đó nếu muốn giữ dữ liệu qua `docker compose down` (xem [docker-volumes-networks.md](docker-volumes-networks.md)).

## 6. Các mẫu sử dụng phổ biến

### 6.1. Cache-aside (lazy loading), phổ biến nhất

```
Đọc:
  1. GET key từ Redis
  2. Có (cache hit)  → trả về
  3. Không (miss)    → query MySQL → SET key value EX <ttl> → trả về

Ghi:
  1. Ghi vào MySQL
  2. DEL key (xóa cache, lần đọc sau sẽ nạp lại)
```

Ưu tiên **xóa** cache khi ghi thay vì **cập nhật** cache: tránh hai request ghi đồng thời làm cache lưu giá trị cũ.

Các vấn đề cần biết:

| Vấn đề | Mô tả | Cách xử lý |
|---|---|---|
| **Dữ liệu cũ** (stale) | Cache chưa bị xóa sau khi DB đổi | TTL ngắn làm lưới an toàn; xóa cache ở mọi nơi ghi |
| **Cache stampede** | Key nóng hết hạn, hàng trăm request cùng query DB | Khóa khi nạp lại; `HybridCache` của .NET tự gộp các request (xem [mục 9](#9-dùng-redis-trong-aspnet-core)) |
| **Cache penetration** | Truy vấn liên tục key không tồn tại → luôn miss | Cache cả kết quả "không có" với TTL ngắn |
| **Đồng loạt hết hạn** | Nhiều key set cùng lúc, hết hạn cùng lúc | Cộng thêm TTL ngẫu nhiên (jitter) |

### 6.2. Các mẫu khác

| Mẫu | Kiểu dữ liệu / lệnh |
|---|---|
| Session đăng nhập dùng chung giữa nhiều instance API | String/Hash + TTL |
| Rate limiting (ví dụ 100 request/phút/IP) | `INCR` + `EXPIRE`, hoặc sorted set cho sliding window |
| Khóa phân tán (distributed lock) | `SET key token NX PX 30000`, nhả khóa bằng Lua kiểm tra token |
| Hàng đợi công việc nền | List (`RPUSH`/`BLPOP`) hoặc Stream |
| Bảng xếp hạng | Sorted set |
| Phát sự kiện realtime giữa các server (SignalR backplane) | Pub/Sub |

## 7. Transaction, pipeline, Lua, pub/sub

- **Pipeline**: gửi nhiều lệnh một lượt, nhận kết quả một lượt, giảm số vòng mạng (round-trip). Không đảm bảo nguyên tử.
- **MULTI / EXEC**: gom lệnh, thực thi liền một khối, không lệnh client khác chen vào. **Không có rollback**: lệnh lỗi giữa chừng thì các lệnh khác vẫn chạy. Kết hợp `WATCH key` để có optimistic locking (EXEC bị hủy nếu key bị đổi).
- **Lua script** (`EVAL`) / **Functions**: chạy logic nhiều bước ngay trên server, nguyên tử. Dùng khi cần "đọc rồi ghi có điều kiện".
- **Pub/Sub**: `PUBLISH channel msg` / `SUBSCRIBE channel`. Kiểu "bắn và quên": subscriber offline thì mất tin. Cần đảm bảo nhận được thì dùng Stream.

## 8. Thử nhanh với Docker

```powershell
# Chạy Redis tạm thời (xóa khi dừng). Ghim tag cụ thể, kiểm tra tag mới nhất trên Docker Hub.
docker run --rm -d --name redis-lab -p 6379:6379 redis:8.2-alpine

# Mở redis-cli bên trong container
docker exec -it redis-lab redis-cli
```

Trong `redis-cli`:

```
127.0.0.1:6379> PING
PONG
127.0.0.1:6379> SET hello world EX 60
OK
127.0.0.1:6379> TTL hello
(integer) 58
127.0.0.1:6379> INFO memory          # xem RAM đang dùng
127.0.0.1:6379> MONITOR              # xem mọi lệnh đang chạy (chỉ dùng khi debug, rất tốn)
```

Dọn dẹp: `docker stop redis-lab`.

Nếu sau này thêm vào `docker-compose.yml` của RoomSplit, dạng tham khảo (theo quy ước ở [docker-compose.md](docker-compose.md): file chung không khai báo `ports`, port dev đặt ở override):

```yaml
services:
  redis:
    image: redis:8.2-alpine
    command: ["redis-server", "--maxmemory", "128mb", "--maxmemory-policy", "allkeys-lru"]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 10
```

API trong compose kết nối qua tên service: `redis:6379`.

## 9. Dùng Redis trong ASP.NET Core

| Package | Vai trò |
|---|---|
| `StackExchange.Redis` | Client cấp thấp, dùng trực tiếp mọi lệnh Redis |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | Cài đặt `IDistributedCache` dùng Redis |
| `Microsoft.Extensions.Caching.Hybrid` | `HybridCache` (.NET 9+): cache 2 tầng (RAM của process + Redis), chống stampede, tag để xóa theo nhóm |

Các package này **không** kéo theo EF Core, nên không vi phạm ràng buộc EF Core 9.0.x trong CLAUDE.md. Khi thêm, ghim version trong `Directory.Packages.props` như mọi package khác.

Phác thảo (chưa áp dụng vào dự án):

```csharp
// Program.cs
builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration = builder.Configuration.GetConnectionString("Redis"); // "localhost:6379"
    o.InstanceName = "roomsplit:";                                          // tiền tố key
});
builder.Services.AddHybridCache(); // tự dùng IDistributedCache (Redis) làm tầng 2
```

```csharp
// Trong một service, ví dụ CategoryService
public Task<IReadOnlyList<CategoryResponse>> GetAllAsync(CancellationToken ct) =>
    cache.GetOrCreateAsync(
        "categories:all",
        async token => await db.Categories
            .OrderBy(c => c.Id)
            .Select(c => new CategoryResponse(c.Id, c.Name))
            .ToListAsync(token) as IReadOnlyList<CategoryResponse>,
        cancellationToken: ct);
```

Ghi chú:
- `ConnectionMultiplexer` của StackExchange.Redis là thread-safe và tốn kém khi tạo: đăng ký **singleton**, dùng chung toàn app (các hàm `AddStackExchangeRedisCache` đã tự làm việc này).
- Không cache entity EF; cache DTO (đúng quy ước "không expose entity" của dự án).
- Health check: package `AspNetCore.HealthChecks.Redis` có thể thêm vào `/health/ready`.

## 10. Replication, Sentinel, Cluster

| Mô hình | Mô tả | Khi nào |
|---|---|---|
| **Standalone** | Một node | Dev, ứng dụng nhỏ, cache chấp nhận mất |
| **Replication** | 1 primary nhận ghi, nhiều replica sao chép (bất đồng bộ) để đọc | Tăng khả năng đọc, có bản sao |
| **Sentinel** | Tiến trình giám sát, tự đưa replica lên làm primary khi primary chết | Cần tính sẵn sàng cao (HA) nhưng dữ liệu vừa một máy |
| **Cluster** | Chia key thành 16384 slot, phân trên nhiều primary (mỗi primary có replica) | Dữ liệu/tải vượt quá một máy |

Trong Cluster, lệnh nhiều key (`MGET`, `SINTER`, MULTI) chỉ chạy được khi các key cùng slot; dùng **hash tag** `{room:1}:balances`, `{room:1}:members` để ép chung slot.

Dịch vụ quản lý sẵn: Azure Cache for Redis / Azure Managed Redis, AWS ElastiCache (Redis/Valkey), Redis Cloud.

## 11. Bảo mật và vận hành

- **Không mở cổng 6379 ra Internet.** Redis bị quét và khai thác rất thường xuyên. Trong compose staging/prod, giống MySQL, không publish port (xem [docker-environments.md](docker-environments.md)).
- Đặt mật khẩu (`requirepass`) hoặc tốt hơn là **ACL** (Redis 6+): mỗi ứng dụng một user, giới hạn lệnh và key pattern.
- Bật TLS khi Redis nằm ở máy khác.
- Đổi tên/tắt lệnh nguy hiểm trên production: `FLUSHALL`, `FLUSHDB`, `KEYS`, `CONFIG`, `DEBUG`.
- Theo dõi: `INFO` (memory, clients, stats: `keyspace_hits`/`keyspace_misses` để tính tỉ lệ cache hit), `SLOWLOG GET`, `LATENCY DOCTOR`.
- Ứng dụng phải chịu được Redis tạm thời không truy cập được: với cache, lỗi Redis nên rơi về query DB thay vì trả 500.

## 12. Redis có cần cho RoomSplit không?

**Ở Phase 1: không.** Lý do:

- Dữ liệu nhỏ (một phòng vài người, vài trăm chi tiêu mỗi tháng), MySQL trả lời trong vài ms với các index đã có (`expenses (room_id, expense_date)`, `settlements (room_id, settled_at)`).
- Chỉ chạy một instance API: nếu cần cache, `IMemoryCache`/`HybridCache` không cấu hình Redis là đủ, không thêm hạ tầng.
- Mỗi thành phần mới = thêm container, cấu hình, giám sát, và thêm nguồn lỗi "cache cũ".

**Khi nào nên cân nhắc:**

| Tình huống tương lai | Redis giúp gì |
|---|---|
| Chạy nhiều instance API sau load balancer | Cache/session dùng chung giữa các instance |
| Thêm authentication | Lưu refresh token, danh sách token bị thu hồi, OTP |
| API public cần chống spam | Rate limiting phân tán |
| Notification, chi tiêu định kỳ tự sinh hàng tháng | Hàng đợi công việc nền, khóa phân tán để job không chạy trùng |
| Màn hình balance được mở rất nhiều | Cache kết quả balance theo phòng, xóa khi có expense/settlement mới |

Dù dùng Redis vào việc gì, **MySQL vẫn là nguồn sự thật** cho expense, share, settlement. Balance luôn tính lại được từ MySQL theo công thức ở CLAUDE.md §9; Redis chỉ giữ bản sao để đọc nhanh.

## 13. Bảng lệnh tra cứu nhanh

| Nhóm | Lệnh |
|---|---|
| Chung | `PING`, `SELECT <db>`, `DBSIZE`, `INFO`, `FLUSHDB` ⚠️ |
| Key | `EXISTS k`, `DEL k`, `UNLINK k` (xóa không chặn), `TYPE k`, `RENAME a b`, `SCAN 0 MATCH roomsplit:* COUNT 100` |
| TTL | `EXPIRE k s`, `PEXPIRE k ms`, `TTL k`, `PERSIST k` |
| String | `SET k v [EX s] [NX\|XX]`, `GET k`, `MGET a b`, `INCR k`, `INCRBY k n`, `APPEND k v` |
| Hash | `HSET k f v`, `HGET k f`, `HGETALL k`, `HDEL k f`, `HINCRBY k f n` |
| List | `LPUSH/RPUSH k v`, `LPOP/RPOP k`, `BLPOP k timeout`, `LRANGE k 0 -1`, `LTRIM k 0 9` |
| Set | `SADD k m`, `SREM k m`, `SMEMBERS k`, `SISMEMBER k m`, `SINTER a b`, `SUNION a b` |
| Sorted set | `ZADD k score m`, `ZRANGE k 0 -1 WITHSCORES`, `ZREVRANGE`, `ZINCRBY k n m`, `ZRANK k m` |
| Transaction | `MULTI`, `EXEC`, `DISCARD`, `WATCH k` |
| Pub/Sub | `PUBLISH ch msg`, `SUBSCRIBE ch` |
| Debug | `MONITOR` ⚠️, `SLOWLOG GET 10`, `MEMORY USAGE k`, `CLIENT LIST` |
