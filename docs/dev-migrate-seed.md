# Tự migrate, chờ DB sẵn sàng và seed data mẫu (Development)

Phạm vi: kiểm tra 3 yêu cầu dưới đây với code hiện tại, phân tích phần còn thiếu và đề xuất phương án.

1. Tự chạy migration khi khởi động, **chỉ ở Development**.
2. Xử lý DB chưa sẵn sàng: `depends_on` + `condition: service_healthy`, hoặc `EnableRetryOnFailure`.
3. Seed data mẫu.

Liên quan: [docker-compose.md](docker-compose.md) (quyết định D: migration trong compose), [docker-multi-env.md](docker-multi-env.md) (staging/prod migrate bằng SQL script).

> **Trạng thái:** đã chọn **A2 + B2 + C2** (2026-10-22) và đã triển khai. Xem [mục 4](#4-kết-quả-triển-khai).

---

## 1. Hiện trạng (kiểm tra 2026-10-22)

| Yêu cầu | Trạng thái | Bằng chứng |
|---|---|---|
| 1. Tự migrate khi khởi động (Dev) | ❌ **Chưa có** | [Program.cs](../RoomSplit.Api/Program.cs) không gọi `Database.Migrate()`. Docs compose chọn D1: chạy `dotnet ef database update` từ host |
| 2a. `depends_on` + `service_healthy` | ✅ **Có** (một lỗ hổng nhỏ, xem 1.1) | [docker-compose.yml](../docker-compose.yml): `api.depends_on.mysql.condition: service_healthy`, `mysql.healthcheck` dùng `mysqladmin ping` |
| 2b. `EnableRetryOnFailure` | ❌ **Chưa có** | `UseMySql(connectionString, new MySqlServerVersion(...))` không có tham số thứ 3 |
| 3. Seed data mẫu | ⚠️ **Một phần** | Chỉ có 6 category qua `HasData` trong [AppDbContext.cs](../RoomSplit.Api/Data/AppDbContext.cs). Đây là *dữ liệu danh mục* (cần ở mọi môi trường), **không phải** data mẫu. Chưa có room/member/expense/settlement mẫu |

### 1.1. Lỗ hổng của healthcheck hiện tại

```yaml
test: ["CMD", "mysqladmin", "ping", "-h", "localhost", "-p$${MYSQL_ROOT_PASSWORD}"]
```

- Với `mysqladmin`, `-h localhost` nghĩa là **kết nối qua Unix socket**, không qua TCP.
- Lần chạy đầu với volume mới, entrypoint của image `mysql` khởi động một **server tạm** (`--skip-networking`, chỉ có socket) để tạo DB `roomsplit`, user, mật khẩu… rồi tắt đi và khởi động server thật.
- Trong lúc server tạm chạy, `ping` qua socket vẫn trả OK → container bị đánh dấu `healthy` **quá sớm**. API khởi động, kết nối TCP tới `mysql:3306` thì bị từ chối.
- Hiện tại chưa lộ ra vì API không đụng DB lúc khởi động. Khi thêm migrate lúc khởi động (yêu cầu 1), lỗi này sẽ xuất hiện đúng lúc quan trọng nhất: máy mới, volume mới.

Sửa: dùng `-h 127.0.0.1` để ép kết nối TCP (server tạm không mở TCP nên ping sẽ thất bại cho tới khi server thật lên).

### 1.2. `depends_on` không đủ một mình

| Tình huống | `depends_on: service_healthy` có giúp không? |
|---|---|
| `docker compose up` lần đầu, API chờ MySQL | ✅ Có |
| Chạy API từ host bằng `dotnet run` / F5 (MySQL trong Docker) | ❌ Không: compose không quản lý process này |
| MySQL restart giữa chừng khi API đang chạy | ❌ Không: `depends_on` chỉ áp dụng lúc khởi động |
| Mạng chập chờn, lỗi tạm thời (deadlock, timeout) | ❌ Không |

`EnableRetryOnFailure` bổ sung cho các trường hợp còn lại: EF tự thử lại khi gặp lỗi tạm thời (transient). Hai cơ chế **bổ sung** cho nhau, không thay thế nhau.

---

## 2. Phương án cho từng yêu cầu

### Quyết định A: Tự migrate khi khởi động

| | A1. `MigrateAsync()` trong Program.cs, chỉ khi `IsDevelopment()` | A2. A1 nhưng bật/tắt bằng config `Database:MigrateOnStartup` (mặc định = `IsDevelopment()`) | A3. Service `migrator` (EF migration bundle) trong compose |
|---|---|---|---|
| Cách làm | Sau `builder.Build()`: tạo scope, lấy `AppDbContext`, `await db.Database.MigrateAsync()` | Giống A1, điều kiện là `app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment())` | Stage Dockerfile tạo `efbundle`, service chạy một lần rồi thoát, `api` chờ `service_completed_successfully` |
| Ưu điểm | Đơn giản nhất, đúng yêu cầu | Cùng kiểu với `Swagger:Enabled` đã có. Dev có thể tắt khi muốn tự kiểm soát (vd. đang thử migration mới) | Cách Microsoft khuyến nghị cho production |
| Nhược điểm | Không tắt được ở Dev khi cần | Thêm một key config (phải cập nhật `appsettings.Development.example.json` nếu ghi rõ) | Không phải "app tự migrate". Phức tạp, không áp dụng khi chạy `dotnet run` từ host |

Lưu ý chung cho A1/A2:
- Áp dụng cả khi chạy từ host (`dotnet run`) lẫn trong container dev (`ASPNETCORE_ENVIRONMENT=Development`, user `root` có quyền DDL).
- Staging/Production **giữ nguyên** cách hiện tại: `dotnet ef migrations script --idempotent` (user `app` không cần quyền DDL).
- EF Core 9 ném lỗi khi `Migrate` mà model đã đổi nhưng chưa tạo migration (`PendingModelChangesWarning`). Đây là lợi ích: quên `migrations add` sẽ bị báo ngay lúc chạy app.

**Đề xuất: A2.** Khác A1 chỉ một dòng, nhưng nhất quán với `Swagger:Enabled` và cho phép tắt.

### Quyết định B: DB chưa sẵn sàng

| | B1. Chỉ `depends_on` (đã có) + sửa healthcheck | B2. B1 + `EnableRetryOnFailure` | B3. B2 + vòng lặp retry tự viết quanh `MigrateAsync()` |
|---|---|---|---|
| Cách làm | Đổi `-h localhost` → `-h 127.0.0.1` | `UseMySql(cs, version, o => o.EnableRetryOnFailure())` | Bọc migrate trong `for` + `Task.Delay` |
| Ưu điểm | Gần như không đổi gì | Che cả các trường hợp ở mục 1.2. Một dòng code. Execution strategy của EF cũng áp dụng cho `MigrateAsync()` | Kiểm soát hoàn toàn số lần, thời gian chờ |
| Nhược điểm | Chạy từ host hoặc MySQL restart vẫn lỗi | Nếu sau này dùng `BeginTransaction()` thủ công phải bọc trong `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`, nếu không EF ném lỗi. Hiện code **không** có chỗ nào dùng transaction thủ công | Trùng chức năng với B2, thêm code phải bảo trì |

Mặc định `EnableRetryOnFailure()`: thử lại tối đa 6 lần, delay tăng dần (tối đa 30 giây). Đủ cho MySQL khởi động. Ngoài ra `restart: unless-stopped` là lớp cuối: nếu migrate vẫn thất bại thì app crash, Docker khởi động lại container.

**Đề xuất: B2.** Một dòng code, bao được các tình huống mà `depends_on` không xử lý. Sửa healthcheck là bắt buộc dù chọn phương án nào.

### Quyết định C: Seed data mẫu

Data mẫu đề xuất (theo ví dụ ở CLAUDE.md §9, tuân thủ business rule §8):
- 1 room "Phòng 101", 3 member Active: An, Binh, Chi.
- Vài expense: điện 300.000 (An trả, EQUAL 3 người → 100.000 mỗi người), nước 100.000 (Binh trả, EQUAL 3 người → phần dư 1 đồng cộng vào người trả), đồ ăn CUSTOM 2 người.
- 1 settlement: Binh trả An 100.000.
- Tổng `share_amount` của mỗi expense **phải bằng** `amount` (rule 1).

| | C1. `HasData` trong `OnModelCreating` | C2. `UseSeeding` / `UseAsyncSeeding` của EF Core 9 | C3. Class `DevDataSeeder` tự viết, gọi trong Program.cs sau migrate |
|---|---|---|---|
| Cách làm | Thêm room/member… vào `HasData`, tạo migration mới | Khai báo trong `AddDbContext(... .UseAsyncSeeding(async (ctx, _, ct) => {...}))`, chỉ đăng ký khi Development. EF tự gọi sau `Migrate()`/`EnsureCreated()` | `Data/DevDataSeeder.cs` có `SeedAsync(AppDbContext, CancellationToken)`, gọi ngay sau `MigrateAsync()` khi Development |
| Ưu điểm | Quen thuộc, đã dùng cho category | Cơ chế chính thức của EF 9. Chạy cả khi dùng `dotnet ef database update` từ host (tool cũng chạy với môi trường Development) | Dễ đọc, dễ hiểu luồng, dễ test riêng |
| Nhược điểm | **Không dùng được**: data đi vào migration → lên cả staging/prod. Phải cố định Id, `created_at`… | Phải viết cả bản sync (`UseSeeding`) lẫn async để `dotnet ef` và app đều chạy được (EF khuyến nghị). Code seed nằm trong Program.cs dài ra (có thể tách sang class) | Chỉ chạy khi app khởi động với migrate bật, `dotnet ef database update` không seed |

Lưu ý chung C2/C3:
- **Idempotent**: chỉ seed khi bảng `rooms` đang rỗng (`if (await db.Rooms.AnyAsync(ct)) return;`), để restart app không tạo trùng.
- Category vẫn giữ `HasData` (dữ liệu danh mục cần ở mọi môi trường). Data mẫu chỉ ở Development.
- Ngày tháng mẫu lấy từ `IAppClock.Today` (không dùng `DateTime.Today`). Với C2, lấy qua `ctx.GetService<IAppClock>()`… phức tạp hơn; có thể dùng ngày cố định cho đơn giản.
- Chưa có `ExpenseService` (Expenses API chưa làm), nên seeder ghi entity trực tiếp và tự đảm bảo rule 1–3.

**Đề xuất: C2**, tách logic ra `Data/DevDataSeeder.cs` (một method sync + một async gọi chung) để Program.cs gọn. Lý do: đây là cơ chế EF 9 thiết kế đúng cho việc này, và seed chạy được cả hai đường (app tự migrate lẫn `dotnet ef database update`). Nếu muốn đơn giản tối đa để học, C3 cũng ổn.

---

## 3. Tổng hợp đề xuất

| Quyết định | Đề xuất | File bị ảnh hưởng |
|---|---|---|
| A. Tự migrate | **A2**: `Database:MigrateOnStartup`, mặc định = `IsDevelopment()` | `Program.cs` |
| B. DB chưa sẵn sàng | **B2**: sửa healthcheck `-h 127.0.0.1` + `EnableRetryOnFailure()` | `docker-compose.yml`, `Program.cs` |
| C. Seed data mẫu | **C2**: `UseSeeding`/`UseAsyncSeeding` chỉ ở Development, logic trong `Data/DevDataSeeder.cs`, idempotent | `Program.cs`, `Data/DevDataSeeder.cs` (mới) |

Không cần migration mới (không đổi schema). Cách kiểm tra sau khi làm: xoá volume dev (`docker compose down -v`, **mất dữ liệu dev hiện tại**) → `docker compose up -d` → API tự tạo schema + data mẫu → `GET /api/v1/rooms` trả về "Phòng 101".

---

## 4. Kết quả triển khai

| File | Thay đổi |
|---|---|
| [Program.cs](../RoomSplit.Api/Program.cs) | `UseMySql(..., mysql => mysql.EnableRetryOnFailure())`. Khi Development: đăng ký `UseSeeding`/`UseAsyncSeeding`. Sau `builder.Build()`: `MigrateAsync()` nếu `Database:MigrateOnStartup` (mặc định = `IsDevelopment()`) |
| [Data/DevDataSeeder.cs](../RoomSplit.Api/Data/DevDataSeeder.cs) (mới) | `Seed` (sync, cho `dotnet ef`) + `SeedAsync` (app), dùng chung `BuildSampleRoom(today)`. Bỏ qua nếu đã có room. Chia EQUAL theo rule 3 (phần dư cho người trả) |
| [docker-compose.yml](../docker-compose.yml) | Healthcheck `mysqladmin ping -h 127.0.0.1` (TCP) thay vì `localhost` (socket) |

### 4.1. Luồng khi chạy ở Development

```
docker compose up  ─► mysql healthy (TCP ping OK)  ─► api khởi động
                                                       │
                     MigrateAsync()  ◄─────────────────┘  (lỗi tạm thời → EF tự retry, tối đa 6 lần)
                          │
                          ├─ áp dụng migration còn thiếu
                          └─ UseAsyncSeeding → DevDataSeeder.SeedAsync
                                 ├─ đã có room → bỏ qua
                                 └─ DB rỗng   → tạo "Phòng 101" + 3 member + 4 expense + 1 settlement
```

### 4.2. Data mẫu và số dư mong đợi

| Expense | Người trả | Số tiền | Cách chia |
|---|---|---|---|
| Điện | An | 300.000 | EQUAL: 100.000 × 3 |
| Nước | Bình | 100.000 | EQUAL: An 33.333, **Bình 33.334** (dư 1 đồng cho người trả), Chi 33.333 |
| Internet | An | 240.000 | EQUAL: 80.000 × 3 |
| Đồ ăn | Chi | 150.000 | CUSTOM: An 90.000, Chi 60.000 |

Settlement: Bình trả An 100.000.

| Member | Trước settlement | Sau settlement |
|---|---|---|
| An | +236.667 | **+136.667** |
| Bình | −113.334 | **−13.334** |
| Chi | −123.333 | **−123.333** |

Tổng luôn bằng 0. Bộ số này dùng để đối chiếu khi làm Balances API.

Ngày của data mẫu tính lùi từ `IAppClock.Today` (vd. điện là 20 ngày trước), nên seed vào lúc nào cũng nằm trong khoảng gần đây.

### 4.3. Ghi chú

- **DB dev đã có dữ liệu** (đã có room) thì seeder không làm gì. Muốn có data mẫu phải dùng DB rỗng: `docker compose down -v` (**xoá volume dev**) rồi `docker compose up -d`.
- Tắt tự migrate ở Dev: đặt `Database__MigrateOnStartup=false` (biến môi trường) hoặc `"Database": { "MigrateOnStartup": false }` trong `appsettings.Development.json`.
- `dotnet ef database update` từ host cũng seed (tool chạy với môi trường Development, gọi `UseSeeding` bản sync).
- Staging/Production: không migrate khi khởi động, không seed. Vẫn dùng `dotnet ef migrations script --idempotent` (script này không chứa data mẫu).
- Có `EnableRetryOnFailure` thì `BeginTransaction()` thủ công phải nằm trong `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`. Một lần `SaveChangesAsync()` (vd. expense + shares) đã tự là một transaction nên không cần.
- **Đánh đổi của retry** (đo thật, xem [docker-debugging.md kịch bản B](docker-debugging.md#kịch-bản-b-mysql-dừng-khi-api-đang-chạy)): khi MySQL chết hẳn, request cần DB **treo hơn 60 giây** vì EF retry tới 6 lần, thay vì lỗi ngay. Lỗi không tạm thời (sai mật khẩu, `Access denied`) thì **không** retry, app thoát ngay. Nếu sau này muốn lỗi nhanh hơn: `EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null)`.

### 4.4. Kết quả kiểm tra thực tế (2026-10-22)

Chạy trên project compose tạm `roomsplit-debuglab` (volume mới, cổng 18080), đã xoá sau khi xong, không đụng DB dev.

| # | Kiểm tra | Kết quả |
|---|---|---|
| 1 | `up -d --build` với volume rỗng | ✅ MySQL `healthy` (healthcheck TCP), rồi API khởi động. Log: `GET_LOCK('__roomsplit_EFMigrationsLock')`, `Applying migration '20261019155628_InitialCreate'`… |
| 2 | `GET /api/v1/rooms` | ✅ `[{"id":1,"name":"Phòng 101",...,"activeMemberCount":3}]`. `/health/ready` → 200 |
| 3 | `restart api` rồi đếm bản ghi | ✅ Không trùng: 1 room, 3 member, 4 expense, 11 share, 1 settlement |
| 4 | Tổng share mỗi expense | ✅ 300000/300000, 100000/100000, 240000/240000, 150000/150000 (rule 1) |
| 5 | Sai mật khẩu DB | App thoát ngay ở `MigrateAsync()` (`Access denied`), crash loop `Restarting (139)`, không retry |
| 6 | Dừng MySQL khi API đang chạy | API vẫn `Up`, `/health/ready` 503, request treo hơn 60 giây (retry). Bật lại MySQL → tự hồi phục, 200 |

