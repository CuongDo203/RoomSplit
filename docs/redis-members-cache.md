# Cache danh sách member bằng Redis (`IDistributedCache`)

Tài liệu phân tích trước khi triển khai (CLAUDE.md §12). Kiến thức nền về Redis: [redis-basics.md](redis-basics.md).

> ✅ **Đã triển khai ngày 2026-10-24** theo bộ khuyến nghị A1 + B1 + C1 + D2 + E1 + F1/F2, có một bổ sung so với phân tích ban đầu: cơ chế ngắt mạch 30 giây khi Redis lỗi ([mục 8.2](#82-bổ-sung-ngắt-mạch-khi-redis-lỗi)). Kết quả đo ở [mục 9](#9-kết-quả-đo).

## Mục lục
1. [Yêu cầu](#1-yêu-cầu)
2. [Hiện trạng code](#2-hiện-trạng-code)
3. [Các quyết định cần chọn](#3-các-quyết-định-cần-chọn)
   - [A. Đặt logic cache ở đâu](#a-đặt-logic-cache-ở-đâu)
   - [B. Thiết kế key và cách xóa](#b-thiết-kế-key-và-cách-xóa)
   - [C. Endpoint nào được cache](#c-endpoint-nào-được-cache)
   - [D. Khi Redis lỗi](#d-khi-redis-lỗi)
   - [E. Service redis trong compose](#e-service-redis-trong-compose)
   - [F. Cách đo có/không cache](#f-cách-đo-cókhông-cache)
4. [Edge case](#4-edge-case)
5. [Cấu hình và biến môi trường mới](#5-cấu-hình-và-biến-môi-trường-mới)
6. [Thay đổi dự kiến theo file](#6-thay-đổi-dự-kiến-theo-file)
7. [Tóm tắt khuyến nghị](#7-tóm-tắt-khuyến-nghị)
8. [Kết quả triển khai](#8-kết-quả-triển-khai)
9. [Kết quả đo](#9-kết-quả-đo)

---

## 1. Yêu cầu

1. Thêm service `redis` vào docker compose.
2. Dùng `IDistributedCache` (package `Microsoft.Extensions.Caching.StackExchangeRedis`).
3. Cache kết quả GET members, **TTL 60 giây**.
4. **Xóa cache** khi member của room thay đổi.
5. Đo thời gian phản hồi **có cache** và **không cache**, mỗi trường hợp **ít nhất 5 lần**.
6. README đầy đủ: phần mềm cần cài, các bước chạy, biến môi trường, sơ đồ các container.

## 2. Hiện trạng code

| Endpoint | Service method | Ghi DB? |
|---|---|---|
| `GET /api/v1/rooms/{roomId}/members?status=` | `MemberService.GetByRoomAsync` (kiểm tra room tồn tại → query members, lọc `status` tùy chọn) | Không |
| `GET /api/v1/members/{id}` | `GetByIdAsync` | Không |
| `POST /api/v1/rooms/{roomId}/members` | `CreateAsync` | Thêm member |
| `PUT /api/v1/members/{id}` | `UpdateAsync` | Sửa tên, số điện thoại |
| `POST /api/v1/members/{id}/leave` | `LeaveAsync` | Đổi `Status`, `LeftAt` |
| `DELETE /api/v1/members/{id}` | `DeleteAsync` | Xóa member |
| `DELETE /api/v1/rooms/{id}` | `RoomService.DeleteAsync` | Xóa room (chỉ khi room **không có** member) |

Không có nơi nào khác ghi vào bảng `members` (ngoài `DevDataSeeder` chạy lúc khởi động trên DB trống).

## 3. Các quyết định cần chọn

### A. Đặt logic cache ở đâu

| Phương án | Mô tả | Ưu | Nhược |
|---|---|---|---|
| **A1. Ngay trong `MemberService`** | Inject `IDistributedCache` vào `MemberService`; `GetByRoomAsync` đọc cache trước; các method ghi xóa key sau `SaveChangesAsync` | Đơn giản nhất; logic đọc và xóa nằm cạnh nhau nên khó quên xóa; đúng quy ước "rule nằm trong service" | `MemberService` thêm một trách nhiệm |
| **A2. Decorator `CachedMemberService : IMemberService`** | Bọc `MemberService`, chỉ lo cache; đăng ký DI thủ công | Tách bạch, tắt cache bằng cách bỏ decorator | Thêm một class phải forward cả 6 method; `RoomService.DeleteAsync` vẫn phải tự xóa key (xem §4) nên logic cache vẫn rải ra |
| **A3. Output caching middleware (Redis store)** | `[OutputCache]` trên action, xóa theo tag | Không đụng service | **Không dùng `IDistributedCache`** như yêu cầu; cache cả HTTP response; cần .NET package khác |

Thêm một helper nhỏ `MemberCacheKeys` (tên key) và đọc/ghi JSON qua extension cho `IDistributedCache` để `RoomService` dùng lại tên key.

**Khuyến nghị: A1.** Dự án nhỏ, chỉ một service bị ảnh hưởng; A2 là trừu tượng hóa sớm, A3 không đáp ứng yêu cầu.

### B. Thiết kế key và cách xóa

Endpoint list có tham số `status` (không có / `Active` / `Left`), nên một room có tới 3 kết quả khác nhau.

| Phương án | Key | Xóa khi đổi | Ưu | Nhược |
|---|---|---|---|---|
| **B1. Một key/room, chứa danh sách đầy đủ; lọc `status` trong RAM** | `roomsplit:members:room:{roomId}` | `Remove` 1 key | 1 key/room, xóa 1 lệnh; cả 3 biến thể `status` dùng chung một lần đọc DB | Lọc trong RAM sau khi đọc cache (danh sách phòng trọ chỉ vài người nên không đáng kể) |
| **B2. Một key cho mỗi (room, status)** | `...:room:{roomId}:all`, `:Active`, `:Left` | `Remove` 3 key | Cache đúng kết quả từng query | 3 lần đọc DB để làm nóng cache; thêm giá trị enum mới thì phải nhớ xóa thêm key |
| **B3. Version key** | `...:room:{roomId}:v` (số) + `...:room:{roomId}:v{n}:{status}` | `INCR` version, key cũ tự hết hạn | Không cần biết danh sách key | `IDistributedCache` **không có** `INCR`, phải dùng thẳng StackExchange.Redis; mỗi lần đọc thêm 1 round-trip |

**Khuyến nghị: B1.** Đơn giản và đúng với dữ liệu nhỏ của một phòng.

### C. Endpoint nào được cache

| Phương án | Mô tả | Ưu | Nhược |
|---|---|---|---|
| **C1. Chỉ `GET /rooms/{roomId}/members`** | Đúng ý "member của room thay đổi thì xóa cache" | Một loại key, invalidation rõ ràng | `GET /members/{id}` vẫn query DB (truy vấn theo khóa chính, rất nhanh) |
| **C2. Cả `GET /members/{id}`** | Thêm key `roomsplit:members:{id}` | Cache toàn bộ GET members | Mỗi lần ghi phải xóa 2 key; ít lợi ích vì `FindAsync` theo PK đã nhanh |

**Khuyến nghị: C1.**

### D. Khi Redis lỗi

| Phương án | Mô tả | Ưu | Nhược |
|---|---|---|---|
| **D1. Để lỗi lan ra** | Redis chết → exception → 500 | Không thêm code | Cache (thứ phụ) làm sập API (thứ chính) |
| **D2. Fail-open** | Bắt lỗi Redis khi đọc/ghi/xóa, log Warning, đọc thẳng DB. Connection string dùng `abortConnect=false` và timeout ngắn để request không treo lâu | API vẫn chạy khi Redis chết; đúng nguyên tắc ở [redis-basics §11](redis-basics.md#11-bảo-mật-và-vận-hành) | Xóa cache thất bại → có thể đọc dữ liệu cũ tối đa 60 giây (TTL là lưới an toàn) |

**Khuyến nghị: D2.**

Health check: **không** đưa Redis vào `/health/ready`, vì API vẫn phục vụ được khi Redis chết (fail-open). Nếu đưa vào, Redis sập sẽ làm load balancer rút API khỏi vòng phục vụ, ngược với mục đích của D2.

### E. Service redis trong compose

| Phương án | Mô tả | Ưu | Nhược |
|---|---|---|---|
| **E1. Cache thuần, không lưu xuống đĩa** | `--save "" --appendonly no`, `maxmemory` + `allkeys-lru`, không volume; healthcheck `redis-cli ping`; API `depends_on: redis: service_healthy` | Đúng bản chất cache: mất thì nạp lại từ MySQL; không cần dọn volume | Khởi động lại Redis thì cache trống (không sao) |
| **E2. Có volume + AOF** | Giữ dữ liệu qua restart | Cache "nóng" ngay sau restart | Thêm volume, I/O, cấu hình; vô ích với TTL 60 giây |

Phân bố theo file compose (giữ quy ước hiện tại):

| File | Nội dung redis |
|---|---|
| `docker-compose.yml` | image ghim `redis:8.2.x-alpine` (tag chính xác chốt lúc triển khai), command, healthcheck; `api.depends_on.redis` |
| `docker-compose.override.yml` (dev) | `container_name: roomsplit-redis`, port `${REDIS_PORT:-6379}:6379` (để `dotnet run` trên host và đo bằng `redis-cli`); `ConnectionStrings__Redis: redis:6379` |
| `docker-compose.prod.yml` | **không** publish port; `--requirepass ${REDIS_PASSWORD}`; giới hạn tài nguyên; log rotation; `ConnectionStrings__Redis: redis:6379,password=${REDIS_PASSWORD}` |

**Khuyến nghị: E1.** Dev không đặt mật khẩu (giống MySQL dev dùng root), staging/prod bắt buộc `REDIS_PASSWORD`.

### F. Cách đo có/không cache

Cần định nghĩa "không cache" sao cho phép đo công bằng:

| Phương án | Cách làm | Đo được gì | Nhược |
|---|---|---|---|
| **F1. Cờ cấu hình `Cache:Enabled`** | `Cache__Enabled=false` → bỏ qua Redis hoàn toàn; restart api để bật/tắt | **Baseline thật**: chỉ có DB | Phải restart container giữa hai lượt đo |
| **F2. Xóa key trước mỗi request** | `redis-cli DEL` rồi gọi API | **Cache miss**: DB + ghi Redis (trường hợp xấu nhất khi có cache) | Không phải "không có cache" |
| **F3. Header bỏ qua cache** (`Cache-Control: no-cache`) | Service đọc header | Đo không cần restart | Thêm hành vi lạ vào API production; service phải biết về HTTP |

**Khuyến nghị: F1 + F2**, đo 3 kịch bản, mỗi kịch bản **10 lần** (≥ 5 theo yêu cầu) sau 1 lần làm nóng (warm-up, bỏ khỏi kết quả):

| Kịch bản | Thiết lập |
|---|---|
| Không cache | `Cache__Enabled=false` |
| Cache miss | Cache bật, `DEL` key trước mỗi request |
| Cache hit | Cache bật, key đã có |

Công cụ: script PowerShell `scripts/measure-members-cache.ps1` gọi `curl.exe -w "%{time_total}"` vào API trong compose (`http://localhost:8080`), in từng lần đo và min / trung bình / trung vị / max. Kết quả thật ghi vào mục "Kết quả đo" của tài liệu này kèm cấu hình máy, ngày đo.

Lưu ý trước khi đọc kết quả: dữ liệu mẫu chỉ có vài member, MySQL nằm cùng máy, nên query DB vốn đã chỉ mất vài ms. Chênh lệch có/không cache sẽ **nhỏ** (cỡ ms) và nhiễu mạng loopback có thể lớn ngang chênh lệch. Đó là kết quả trung thực, cũng là minh họa cho kết luận "Phase 1 chưa thật sự cần Redis" ở [redis-basics §12](redis-basics.md#12-redis-có-cần-cho-roomsplit-không).

## 4. Edge case

| Tình huống | Xử lý |
|---|---|
| Room không tồn tại | Không cache; vẫn trả 404 như hiện tại (kiểm tra room chỉ chạy khi cache miss) |
| Room có 0 member được cache `[]`, rồi room bị xóa | `RoomService.DeleteAsync` cũng xóa key members của room đó; nếu không, trong 60 giây `GET` vẫn trả `200 []` thay vì 404 |
| Hai room khác nhau | Key theo `roomId` nên không ảnh hưởng nhau |
| `UpdateAsync` / `LeaveAsync` / `DeleteAsync` chỉ có `id` member | Lấy `member.RoomId` từ entity đã load (đã có sẵn trong `FindMemberAsync`) để biết key cần xóa |
| Thao tác ghi thất bại (404, 409, 400) | Không xóa cache (xóa sau `SaveChangesAsync` thành công) |
| Race: request đọc lấy dữ liệu cũ từ DB, request ghi xóa key, request đọc ghi dữ liệu cũ vào cache | Có thể cũ tối đa 60 giây. Chấp nhận ở MVP; TTL là lưới an toàn (giải pháp chặt hơn: version key B3 hoặc khóa) |
| Redis chết / timeout | Fail-open (D2), log Warning |
| Dữ liệu cache sai định dạng (đổi `MemberResponse` giữa các bản deploy) | Deserialize lỗi → coi như miss, ghi đè. Có thể thêm hậu tố version vào key (`:v1`) |
| Serialize | `System.Text.Json` với `JsonSerializerDefaults.Web`; `DateOnly` và enum được hỗ trợ sẵn |

## 5. Cấu hình và biến môi trường mới

| Key cấu hình | Biến môi trường | Mặc định | Ý nghĩa |
|---|---|---|---|
| `ConnectionStrings:Redis` | `ConnectionStrings__Redis` | (trống) | Chuỗi kết nối StackExchange.Redis. Trống → không đăng ký Redis, dùng cache trong RAM của process (`AddDistributedMemoryCache`), để `dotnet run` vẫn chạy khi không có Redis |
| `Cache:Enabled` | `Cache__Enabled` | `true` | Tắt hẳn cache (dùng để đo baseline) |
| `Cache:MembersTtlSeconds` | `Cache__MembersTtlSeconds` | `60` | TTL cache members |
| (compose) | `REDIS_PORT` | `6379` | Port host của Redis dev (`.env`) |
| (compose) | `CACHE_ENABLED` | `true` | Chuyển thành `Cache__Enabled` của container api dev; script đo đặt `false` cho kịch bản "không cache" |
| (compose) | `REDIS_PASSWORD` | bắt buộc ở staging/prod | Mật khẩu Redis (`envs/*.env`) |

Chuỗi kết nối gợi ý: `redis:6379,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000`.

## 6. Thay đổi dự kiến theo file

| File | Thay đổi |
|---|---|
| `Directory.Packages.props` | `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.x (không kéo EF Core; kiểm tra lại bằng `dotnet restore`) |
| `RoomSplit.Api.csproj` | `PackageReference` không version |
| `Program.cs` | `AddStackExchangeRedisCache` (hoặc `AddDistributedMemoryCache` khi không có connection string), bind `Cache` options |
| `Common/CacheOptions.cs`, `Services/Members/MemberCacheKeys.cs` | Options và tên key |
| `Services/Members/MemberService.cs` | Đọc cache trong `GetByRoomAsync`, xóa key sau mỗi lần ghi, fail-open |
| `Services/Rooms/RoomService.cs` | Xóa key members khi xóa room |
| `appsettings*.json`, `appsettings.Development.example.json` | `Cache` section, `ConnectionStrings:Redis` |
| `docker-compose*.yml`, `.env.example`, `envs/*.env.example` | Service redis, biến mới |
| `scripts/measure-members-cache.ps1` | Script đo |
| `README.md` | Phần mềm cần cài, các bước chạy, bảng biến môi trường, sơ đồ container |
| `CLAUDE.md` | Cập nhật tech stack, cấu trúc, tiến độ |

## 7. Tóm tắt khuyến nghị

| Quyết định | Chọn |
|---|---|
| A. Vị trí logic cache | **A1**: trong `MemberService` |
| B. Key | **B1**: một key/room, lọc `status` trong RAM |
| C. Phạm vi | **C1**: chỉ `GET /rooms/{roomId}/members` |
| D. Redis lỗi | **D2**: fail-open, không đưa vào `/health/ready` |
| E. Compose | **E1**: cache thuần, không volume; mật khẩu ở staging/prod |
| F. Đo | **F1 + F2**: không cache / miss / hit, mỗi kịch bản 10 lần |

## 8. Kết quả triển khai

### 8.1. Code

| File | Nội dung |
|---|---|
| [Common/CacheOptions.cs](../RoomSplit.Api/Common/CacheOptions.cs) | Section `Cache`: `Enabled`, `MembersTtlSeconds` |
| [Services/Members/IMemberCache.cs](../RoomSplit.Api/Services/Members/IMemberCache.cs), [MemberCache.cs](../RoomSplit.Api/Services/Members/MemberCache.cs) | Đọc/ghi/xóa danh sách member của một room trên `IDistributedCache`, JSON (`JsonSerializerDefaults.Web`), TTL tuyệt đối, fail-open. Đăng ký **singleton** |
| [Services/Members/MemberService.cs](../RoomSplit.Api/Services/Members/MemberService.cs) | `GetByRoomAsync`: đọc cache → miss thì kiểm tra room, query, ghi cache → lọc `status` trong RAM. `Create`/`Update`/`Leave`/`Delete`: xóa key sau `SaveChangesAsync` |
| [Services/Rooms/RoomService.cs](../RoomSplit.Api/Services/Rooms/RoomService.cs) | `DeleteAsync`: xóa key members của room |
| [Program.cs](../RoomSplit.Api/Program.cs) | `AddStackExchangeRedisCache` (`InstanceName = "roomsplit:"`) khi có `ConnectionStrings:Redis`, ngược lại `AddDistributedMemoryCache` |

So với phương án A1 "logic cache ngay trong `MemberService`": phần thao tác với `IDistributedCache` (tên key, serialize, bắt lỗi) được gom vào `IMemberCache` để `RoomService` dùng chung và chỉ viết fail-open một lần. Quyết định *khi nào* đọc/xóa cache vẫn nằm trong `MemberService`/`RoomService`.

Key thực tế trong Redis: `roomsplit:members:room:{roomId}`. `RedisCache` của Microsoft lưu mỗi entry dưới dạng **hash** (field `data` chứa JSON, `absexp`, `sldexp`), nên xem nội dung bằng `HGET roomsplit:members:room:3 data`, không phải `GET`.

`InvalidateRoomAsync` cố ý **không** nhận `CancellationToken`: dữ liệu đã lưu xong, nếu client ngắt kết nối đúng lúc này thì việc xóa cache vẫn phải chạy.

### 8.2. Bổ sung: ngắt mạch khi Redis lỗi

Phát hiện khi thử tắt Redis (`docker stop roomsplit-redis`): fail-open đúng là vẫn trả 200, nhưng **mỗi request mất ~3,9 giây** vì phải chờ hết timeout của cả lần đọc lẫn lần ghi cache (`connectTimeout=2000`, `syncTimeout/asyncTimeout=1000`). Như vậy còn chậm hơn nhiều so với không có cache.

Giải pháp: sau một lỗi Redis, `MemberCache` **bỏ qua cache 30 giây** (`RetryAfter`) rồi mới thử lại. Dùng `TimeProvider` đã đăng ký sẵn nên test có thể thay bằng `FakeTimeProvider`.

| Thử nghiệm (2026-10-24) | Trước khi có ngắt mạch | Sau khi có ngắt mạch |
|---|---|---|
| Redis tắt, 3–4 request liên tiếp | 3,92 s / 3,84 s / 3,89 s | 8,47 s (request đầu, app vừa khởi động chưa từng kết nối được) / 0,016 s / 0,013 s / 0,009 s |
| Redis bật lại | | Sau 30 giây tự dùng lại cache (key được ghi lại) |

Đánh đổi: trong 30 giây bỏ qua, việc xóa cache cũng bị bỏ qua. Nếu Redis chỉ chập chờn (không khởi động lại, còn giữ dữ liệu), danh sách cũ có thể còn tối đa 60 giây (TTL). Nếu Redis khởi động lại thì không có vấn đề này, vì Redis không lưu xuống đĩa nên dữ liệu cũ đã mất hết.

### 8.3. Kiểm tra chức năng (2026-10-24, room tạm, đã xóa sau khi kiểm tra)

| Bước | Key `roomsplit:members:room:{id}` |
|---|---|
| `GET` members của room không tồn tại → 404 | Không tạo |
| `GET` room rỗng → `[]` | Có, `TTL` = 59 |
| `POST` member | Bị xóa |
| `GET` | Có lại |
| `PUT` member | Bị xóa |
| `POST .../leave` | Bị xóa |
| `GET ?status=Left` rồi `?status=Active` | Một key phục vụ cả hai |
| `POST .../leave` lần 2 → 409 | Giữ nguyên (thao tác lỗi không xóa cache) |
| `DELETE` member | Bị xóa |
| `GET` (room rỗng, cache `[]`) rồi `DELETE` room → 204 | Bị xóa; `GET` members ngay sau đó → **404** (không còn `200 []`) |

## 9. Kết quả đo

### 9.1. Môi trường và cách đo

- Ngày 2026-10-24. Máy dev: AMD Ryzen 5 6600H, 15 GB RAM, Windows 11, Docker Engine 29.7.2 (Docker Desktop, WSL2).
- Stack dev compose: `mysql:8.0.46`, `redis:8.2.10-alpine`, API image build từ source (`ASPNETCORE_ENVIRONMENT=Development`, log Information).
- Endpoint: `GET /api/v1/rooms/3/members` (room 3 có 1 member, dữ liệu dev hiện có).
- Script: [scripts/measure-members-cache.ps1](../scripts/measure-members-cache.ps1), 10 lần mỗi kịch bản, mỗi kịch bản có 1 request làm nóng không tính. Thời gian là `time_total` của `curl.exe` chạy trên Windows, gọi qua cổng 8080 được Docker Desktop chuyển vào container (gồm cả thời gian mở kết nối TCP).

```powershell
./scripts/measure-members-cache.ps1 -RoomId 3 -Runs 10
```

### 9.2. Số liệu

**Lượt 1** (ms):

| Kịch bản | Từng lần đo | Min | Trung bình | Trung vị | Max |
|---|---|---|---|---|---|
| Không cache | 8.70, 10.47, 9.05, 8.98, 9.46, 11.62, 9.54, 10.01, 12.52, 10.57 | 8.70 | 10.09 | 9.77 | 12.52 |
| Cache miss | 32.65, 18.53, 12.68, 15.30, 15.92, 15.86, 16.11, 12.31, 13.76, 17.03 | 12.31 | 17.01 | 15.89 | 32.65 |
| Cache hit | 5.38, 6.18, 5.49, 8.13, 7.57, 9.42, 7.03, 6.40, 6.47, 5.66 | 5.38 | 6.77 | 6.44 | 9.42 |

**Lượt 2** (ms):

| Kịch bản | Từng lần đo | Min | Trung bình | Trung vị | Max |
|---|---|---|---|---|---|
| Không cache | 16.50, 10.70, 8.41, 9.27, 8.65, 10.24, 10.54, 10.56, 12.86, 11.70 | 8.41 | 10.94 | 10.55 | 16.50 |
| Cache miss | 20.95, 15.84, 13.19, 11.37, 14.29, 14.53, 13.63, 13.33, 15.15, 13.18 | 11.37 | 14.55 | 13.96 | 20.95 |
| Cache hit | 6.29, 5.95, 6.16, 5.40, 5.27, 5.64, 7.17, 7.57, 5.96, 7.47 | 5.27 | 6.29 | 6.06 | 7.57 |

**Mức sàn**: `GET /health/live` (không chạm DB hay Redis), 10 lần: 3.75, 3.14, 4.87, 3.67, 3.95, 3.64, 5.03, 4.23, 4.52, 4.58 → trung vị **4.09 ms**. Đây là chi phí cố định của curl + chuyển cổng Docker Desktop + pipeline ASP.NET Core mà mọi request đều phải trả.

### 9.3. Nhận xét

| | Trung vị (2 lượt) | Trừ mức sàn ~4,1 ms (≈ phần việc riêng của endpoint) |
|---|---|---|
| Không cache | 9,8–10,6 ms | ~6 ms: 2 query MySQL (kiểm tra room tồn tại + lấy members) |
| Cache hit | 6,1–6,4 ms | ~2 ms: 1 lệnh Redis (`HMGET`) + deserialize JSON |
| Cache miss | 14,0–15,9 ms | ~10–12 ms: đọc Redis (trượt) + 2 query MySQL + ghi Redis |

- **Cache hit nhanh hơn không cache khoảng 4 ms (~40% thời gian phản hồi)**, hay khoảng 3 lần nếu chỉ tính phần việc riêng của endpoint.
- **Cache miss chậm hơn không cache khoảng 4–6 ms**, vì thêm 2 lượt đi về Redis. Cache chỉ có lợi khi tỉ lệ hit cao: dữ liệu được đọc nhiều lần trong 60 giây giữa hai lần thay đổi. Danh sách member của phòng trọ ít thay đổi nên thỏa điều kiện này.
- Con số tuyệt đối rất nhỏ vì dữ liệu chỉ có 1 member, MySQL ở cùng máy và đã có index. Chênh lệch sẽ lớn hơn khi query nặng hơn (ví dụ balances cần SUM qua nhiều bảng) hoặc khi DB ở máy khác. Với Phase 1, kết quả này khớp với nhận định ở [redis-basics §12](redis-basics.md#12-redis-có-cần-cho-roomsplit-không): Redis đúng là có giúp, nhưng chưa phải nhu cầu cấp thiết.
- Hạn chế của phép đo: chỉ 10 mẫu/kịch bản, đo phía client trên cùng máy, có nhiễu (request đầu của kịch bản miss lượt 1 lên 32 ms). Muốn kết luận chắc hơn cần nhiều mẫu hơn và công cụ tải chuyên dụng (k6, bombardier) với nhiều request đồng thời.
