# Đề xuất cải thiện kiến trúc (điểm 2 → 8)

Tài liệu này phân tích 7 điểm cần cải thiện được nêu trong buổi review cấu trúc project, đưa ra các phương án cho từng điểm và phương án khuyến nghị. Điểm 1 (project test) sẽ có tài liệu riêng.

Bối cảnh: MVP, một project `RoomSplit.Api`, EF Core 9.0.x + Pomelo 9.0.0, mới có API Rooms / Members / Categories. Expense và Settlement API chưa làm, nên bảng `expenses`, `settlements` hiện chưa có dữ liệu thật.

Tóm tắt khuyến nghị:

| # | Vấn đề | Khuyến nghị | Công sức | Nên làm khi nào |
|---|---|---|---|---|
| 2 | Wildcard `9.0.*` | Central Package Management + pin version | Nhỏ | Ngay |
| 3 | `settlements` thiếu `room_id` | Thêm cột `room_id` NOT NULL + FK | Nhỏ | Ngay, trước Settlements API |
| 4 | Race condition khi xóa → 500 | Bắt `DbUpdateException` (MySQL 1451) trong handler chung | Nhỏ | Ngay |
| 5 | Không truyền `CancellationToken` | Thêm `CancellationToken ct` vào mọi method async | Nhỏ | Ngay |
| 6 | `DateTime.Today` phụ thuộc múi giờ server | Inject `TimeProvider` + cấu hình múi giờ phòng | Nhỏ | Ngay |
| 7 | Chưa phân trang | `PagedResponse<T>` offset-based, áp dụng từ Expenses API | Vừa | Cùng Expenses API |
| 8 | Vận hành (health check, Docker, CI, build props) | Làm từng phần, theo thứ tự ưu tiên | Vừa | Dần dần |

---

## 2. Version package dùng wildcard

### Hiện trạng
`RoomSplit.Api.csproj` dùng `9.0.*` cho `EFCore.NamingConventions` và `Microsoft.EntityFrameworkCore.Design`. Hiện tại đang resolve ra:

| Package | Version resolve được |
|---|---|
| Microsoft.EntityFrameworkCore (transitive) | 9.0.21 |
| Microsoft.EntityFrameworkCore.Design | 9.0.21 |
| EFCore.NamingConventions | 9.0.0 |
| Pomelo.EntityFrameworkCore.MySql | 9.0.0 |

Vấn đề: hai máy restore vào hai thời điểm khác nhau có thể ra hai version khác nhau, nên build không tái lập được. Khi thêm project test (điểm 1), project đó cũng phải dùng đúng EF Core 9.0.x, nên rất dễ lệch version giữa các project.

### Phương án

**A. Pin version cụ thể ngay trong csproj**
- Ưu: sửa 2 dòng, đơn giản nhất.
- Nhược: khi có thêm `RoomSplit.Tests` thì phải nhớ pin cùng version ở nhiều nơi. Lệch version giữa các project là nguồn gây NU1608/NU1107.

**B. Central Package Management (CPM): `Directory.Packages.props` ở root solution** ✅
- Đặt `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`, mọi version khai báo ở một file. Trong csproj chỉ còn `<PackageReference Include="..." />`, không ghi version.
- Ưu: một nguồn sự thật duy nhất cho mọi project. Ràng buộc "EF Core 9.0.x" được ghi rõ một chỗ, kèm comment. Đây là chuẩn phổ biến trong các solution .NET hiện nay.
- Nhược: thêm một file. Dev mới cần biết rằng version không nằm trong csproj.

**C. B + NuGet lock file (`RestorePackagesWithLockFile` + `packages.lock.json`)**
- Ưu: khóa cả transitive dependency. CI có thể dùng `--locked-mode` để fail nếu lock file lệch.
- Nhược: mỗi lần đổi package phải commit thêm lock file. Hay gây conflict khi merge. Với MVP một người làm thì hơi thừa.

### Khuyến nghị: B
Pin đúng các version đang chạy (9.0.21 / 9.0.0) để không đổi hành vi. Có thể thêm lock file (C) sau, khi có CI (điểm 8).

Phác thảo:
```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- Pomelo 9.0.0 chỉ hỗ trợ EF Core 9.0.x: KHÔNG nâng lên 10 -->
    <PackageVersion Include="Pomelo.EntityFrameworkCore.MySql" Version="9.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.21" />
    <PackageVersion Include="EFCore.NamingConventions" Version="9.0.0" />
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.13" />
    <PackageVersion Include="Swashbuckle.AspNetCore.SwaggerUI" Version="10.3.1" />
  </ItemGroup>
</Project>
```

---

## 3. Bảng `settlements` không có `room_id`

### Hiện trạng
`Settlement` chỉ có `PayerId` và `PayeeId`. Muốn lấy settlement của một phòng thì phải join `settlements → members → room_id`. Rule 5 (payer và payee cùng phòng) chỉ được kiểm tra ở service.

Cách này chạy được vì hiện mỗi member chỉ thuộc một phòng. Nhưng scope tương lai có "một member thuộc nhiều phòng" (N–N). Khi đó một settlement giữa An và Bình sẽ không xác định được thuộc phòng nào, và công thức balance theo phòng sẽ sai.

### Phương án

**A. Giữ nguyên, suy ra phòng qua member**
- Ưu: không đổi gì.
- Nhược: query balance và danh sách luôn phải join thêm một bảng. Đến lúc làm N–N thì phải backfill dữ liệu thật, và có trường hợp không backfill được (member ở 2 phòng).

**B. Thêm `room_id` NOT NULL, FK → rooms, Restrict** ✅
- Đồng bộ với `expenses.room_id`. Lọc theo phòng dùng thẳng index `room_id`.
- Service kiểm tra `payer.RoomId == payee.RoomId == request.RoomId`, đúng như cách làm với Expense (rule 2).
- Route gợi ý: `POST /api/v1/rooms/{roomId}/settlements`, `GET /api/v1/rooms/{roomId}/settlements`, thống nhất với `rooms/{roomId}/members`.
- Ưu: bảng hiện chưa có dữ liệu, nên migration NOT NULL không cần giá trị mặc định hay backfill. Sẵn sàng cho N–N.
- Nhược: dữ liệu dư thừa (`room_id` suy ra được từ member) nên có khả năng không nhất quán. Rủi ro này được kiểm soát vì service là nơi duy nhất ghi dữ liệu.

**C. B + composite FK `(payer_id, room_id) → members(id, room_id)`**
- Ưu: DB đảm bảo payer cùng phòng, không chỉ dựa vào service.
- Nhược: cần thêm unique index `(id, room_id)` trên `members`, cấu hình Fluent API phức tạp hơn. Khi chuyển sang N–N thì composite FK này lại phải bỏ. CLAUDE.md quy định rule 2 và rule 5 kiểm tra ở service layer, nên C trái với quy ước.

### Khuyến nghị: B
Migration: `AddRoomIdToSettlements`. Cập nhật CLAUDE.md (bảng schema `settlements`, relationships `rooms 1–N settlements`) và thêm navigation `Room.Settlements`.

---

## 4. Race condition khi xóa trả 500 thay vì 409

### Hiện trạng
`RoomService.DeleteAsync` và `MemberService.DeleteAsync` làm theo kiểu "check rồi mới xóa": kiểm tra lịch sử bằng `AnyAsync`, sau đó mới `Remove`. Nếu có một request tạo expense chen vào giữa hai bước, FK `Restrict` vẫn chặn được việc xóa (dữ liệu an toàn). Nhưng EF ném `DbUpdateException` (MySQL error 1451, *Cannot delete or update a parent row*), và `AppExceptionHandler` bỏ qua loại lỗi này nên client nhận 500.

Tương tự, khi Expense API ra đời, tạo expense cho member vừa bị xóa sẽ gây lỗi 1452 (*Cannot add or update a child row*), và client cũng nhận 500.

### Phương án

**A. Bọc try/catch trong từng method `DeleteAsync`**
- Ưu: rõ ràng, message cụ thể cho từng trường hợp.
- Nhược: lặp code ở mọi service. Dễ quên ở service mới.

**B. Map trong `AppExceptionHandler`: `DbUpdateException` có inner `MySqlException` với mã 1451/1452 → 409** ✅
- Ưu: một chỗ duy nhất, mọi service được bảo vệ. Giữ đúng quy ước "controller không tự trả lỗi". Kiểm tra `AnyAsync` hiện có vẫn giữ để trả message thân thiện cho 99% trường hợp, handler chỉ là lưới an toàn.
- Nhược: message chung chung (ví dụ "The resource is referenced by other data"). Phụ thuộc mã lỗi của MySQL (`MySqlConnector.MySqlErrorCode.RowIsReferenced2`, `NoReferencedRow2`). Chấp nhận được vì DB đã cố định là MySQL.

**C. Dùng transaction `Serializable` cho thao tác xóa**
- Ưu: loại bỏ race hoàn toàn.
- Nhược: nặng, dễ deadlock. Vẫn phải xử lý lỗi deadlock. Thừa so với rủi ro thực tế.

**D. Package `EntityFramework.Exceptions.MySQL.Pomelo`**
- Ưu: chuyển lỗi DB thành exception có kiểu (`ReferenceConstraintException`, `UniqueConstraintException`…).
- Nhược: thêm dependency, phải chọn đúng bản tương thích EF Core 9. Với chỉ 2–3 mã lỗi thì tự map cũng đủ.

### Khuyến nghị: B
Map thêm cả lỗi 1062 (duplicate key) → 409, để dùng sau này cho unique index (ví dụ `categories.name`). Log lỗi gốc ở mức Warning.

---

## 5. Không truyền `CancellationToken`

### Hiện trạng
Không có controller hay service nào nhận `CancellationToken`. Khi client hủy request (đóng tab, timeout), query EF vẫn chạy đến hết, tốn connection pool và CPU của DB. Hiện ảnh hưởng còn nhỏ, nhưng query balance và danh sách expense sẽ nặng hơn.

### Phương án

**A. Thêm tham số `CancellationToken ct = default` vào service, controller truyền `HttpContext.RequestAborted`**
- Nhược: controller phải viết `HttpContext.RequestAborted` ở mọi chỗ, dài dòng.

**B. Action nhận `CancellationToken ct`, ASP.NET Core tự bind từ `RequestAborted`; service nhận `CancellationToken ct` (không default) và truyền vào mọi `ToListAsync`, `SaveChangesAsync`, `FindAsync`…** ✅
- Ưu: đây là pattern chuẩn của ASP.NET Core. Không để default ở service nên trình biên dịch buộc phải truyền, khó quên.
- Nhược: tham số dài thêm một chút. `FindAsync` phải gọi dạng `FindAsync([id], ct)`.

**C. Không làm**
- Chấp nhận được với tải hiện tại, nhưng càng nhiều service thì sửa càng tốn.

### Khuyến nghị: B
Đổi signature cả 3 interface `IRoomService`, `IMemberService`, `ICategoryService`. Service mới (Expenses, Settlements, Balances) theo cùng quy ước. Thêm quy ước này vào CLAUDE.md, mục 11.

Lưu ý: truyền `ct` vào `SaveChangesAsync` là an toàn. `SaveChangesAsync` chạy trong một transaction, nên nếu request bị hủy giữa chừng thì toàn bộ thay đổi được rollback, không bị ghi dở.

---

## 6. `DateTime.Today` phụ thuộc múi giờ server

### Hiện trạng
`MemberService` dùng `DateOnly.FromDateTime(DateTime.Today)` làm mặc định cho `JoinedAt` và `LeftAt`. Expense (`ExpenseDate`) và Settlement (`SettledAt`) cũng sẽ cần "hôm nay". `DateTime.Today` lấy theo giờ local của server:
- Máy dev Windows ở Việt Nam: đúng.
- Server hoặc container Linux (mặc định UTC): từ 0h đến 7h sáng giờ Việt Nam sẽ bị ghi thành **ngày hôm trước**.

Ngoài ra, `DateTime.Today` là static nên không test được các logic phụ thuộc ngày.

### Phương án

**A. Đặt biến môi trường `TZ=Asia/Ho_Chi_Minh` trên server**
- Ưu: không sửa code.
- Nhược: đúng hay sai phụ thuộc cấu hình hạ tầng, dễ quên. Vẫn không test được.

**B. Inject `TimeProvider` (có sẵn trong .NET 8+) + cấu hình múi giờ ứng dụng** ✅
- `builder.Services.AddSingleton(TimeProvider.System)`.
- Thêm setting `App:TimeZone = "Asia/Ho_Chi_Minh"`. Một helper nhỏ, ví dụ `IClock` hoặc extension `TimeProvider.TodayIn(TimeZoneInfo)` trong `Common/`, trả về `DateOnly` hôm nay theo múi giờ đó.
- Ưu: đúng ở mọi môi trường. Test dùng được `FakeTimeProvider` (package `Microsoft.Extensions.TimeProvider.Testing`, không phụ thuộc EF nên an toàn).
- Nhược: thêm một dependency inject vào service.

**C. Lưu múi giờ theo từng phòng (`rooms.time_zone`)**
- Ưu: đúng cả khi có phòng ở nước khác.
- Nhược: thừa với bài toán phòng trọ Việt Nam. Có thể nâng cấp từ B lên sau nếu cần.

**D. Bắt client luôn gửi ngày (bỏ giá trị mặc định)**
- Ưu: server không phải đoán.
- Nhược: đổi contract API, kém tiện cho client.

### Khuyến nghị: B
`created_at` (DATETIME) vẫn để DB sinh bằng `CURRENT_TIMESTAMP(6)`, không đổi. Lưu ý cài đặt: `TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")` chạy được trên cả Windows và Linux từ .NET 6 (có ICU). Image Docker dạng alpine/chiseled cần có ICU/tzdata, sẽ xử lý ở điểm 8. Thêm setting này vào `appsettings.json`, vì không phải secret nên không cần cho vào file example.

---

## 7. Endpoint danh sách chưa phân trang

### Hiện trạng
`GET /rooms` và `GET /rooms/{id}/members` trả toàn bộ dữ liệu. Với room và member thì không sao, vì mỗi phòng chỉ có vài người. Nhưng `GET /rooms/{id}/expenses` sẽ tăng mãi theo thời gian (vài chục bản ghi mỗi tháng, nhân nhiều năm), và settlements cũng vậy.

### Phương án

**A. Offset pagination: `?page=1&pageSize=20`, trả `{ items, page, pageSize, totalCount }`** ✅
- Ưu: đơn giản, client dễ làm UI có số trang. `COUNT(*)` theo `room_id` có index nên rẻ ở quy mô này.
- Nhược: offset lớn thì chậm, và có thể trùng hoặc sót bản ghi khi dữ liệu thay đổi giữa hai lần gọi. Không đáng kể với vài nghìn expense mỗi phòng.

**B. Cursor (keyset) pagination: `?after=<expenseDate,id>&limit=20`**
- Ưu: hiệu năng ổn định, không trùng hay sót.
- Nhược: client không nhảy đến trang N được. Phức tạp hơn khi sắp xếp theo nhiều cột. Thừa với MVP.

**C. Không phân trang, chỉ bắt buộc lọc theo khoảng ngày (`from`, `to`)**
- Ưu: rất hợp với domain ("chi tiêu tháng 10"), đơn giản.
- Nhược: khoảng ngày rộng thì vẫn trả nhiều dữ liệu, không có giới hạn trên.

### Khuyến nghị: A + bộ lọc của C
- `Dtos/PagedResponse<T>` và `PagedQuery` (`Page` ≥ 1, `PageSize` 1–100, mặc định 20) đặt ở `Dtos/` hoặc `Common/`. Thêm một extension `ToPagedAsync(page, pageSize, ct)` cho `IQueryable<T>`.
- Expenses: `GET /api/v1/rooms/{roomId}/expenses?from=&to=&categoryId=&page=&pageSize=`, sắp xếp `expense_date DESC, id DESC`.
- Thêm index `(room_id, expense_date)` cho `expenses` và `(room_id, settled_at)` cho `settlements` (đi kèm điểm 3). Có thể gộp vào cùng migration với điểm 3.
- **Không** đổi `GET /rooms` và `GET /members` bây giờ, vì đổi kiểu trả về là breaking change mà chưa có lợi. Áp dụng ngay từ đầu cho Expenses và Settlements.

---

## 8. Vận hành: health check, Docker, CI, build settings

Đây là 4 việc độc lập. Mỗi việc có phương án riêng.

### 8a. Health check

**A. `AddHealthChecks()` + `AddDbContextCheck<AppDbContext>()` (package `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`)** ✅
- **Bắt buộc dùng bản 9.0.x.** Bản 10.x kéo EF Core 10 theo, gây vỡ restore.
- Map `/health/live` (không check DB) và `/health/ready` (check DB), theo kiểu probe của Kubernetes / Docker.

**B. Package `AspNetCore.HealthChecks.MySql` (cộng đồng)**
- Check được MySQL trực tiếp, nhưng thêm dependency bên ngoài. A đã đủ dùng.

### 8b. Docker

**A. `docker-compose.yml` chỉ chạy MySQL 8.0 cho môi trường dev** ✅ (làm trước)
- Dev mới chạy `docker compose up -d` là có DB, không cần cài MySQL lên máy. Port 3306, volume giữ dữ liệu, password lấy từ `.env` (git-ignored) kèm `.env.example`.

**B. Thêm `Dockerfile` multi-stage cho API** (làm sau, khi cần deploy)
- Dùng image `mcr.microsoft.com/dotnet/aspnet:10.0`, không dùng bản chiseled hay alpine, để có sẵn ICU và tzdata cho điểm 6.
- Migration **không** tự chạy khi app start (`Database.Migrate()` lúc khởi động nguy hiểm khi chạy nhiều instance). Nên tạo migration bundle (`dotnet ef migrations bundle`) hoặc chạy migration như một bước deploy riêng.

### 8c. CI

**A. GitHub Actions: restore → build → test trên mỗi PR vào `develop` / `main`** ✅
- Có thể dùng MySQL service container khi có integration test (điểm 1).
- Khi đã có CPM (điểm 2), có thể bật lock file + `--locked-mode`.

**B. Azure DevOps / GitLab CI**
- Chỉ chọn khi team đang dùng các nền tảng đó. Repo hiện dùng `gh`/GitHub nên chọn A.

### 8d. Build settings chung

**`Directory.Build.props` ở root** ✅ với:
- `Nullable`, `ImplicitUsings`, `TargetFramework` dùng chung (chuyển từ csproj lên).
- `TreatWarningsAsErrors=true` (có thể chỉ bật cho Release/CI để dev không bị chặn khi đang code dở).
- `.editorconfig` với các rule format cơ bản. `dotnet format --verify-no-changes` chạy trong CI.

Phương án thay thế là để nguyên trong từng csproj. Cách đó chấp nhận được khi chỉ có một project, nhưng khi có project test thì sẽ phải lặp cấu hình.

### Thứ tự khuyến nghị cho điểm 8
8d → 8a → 8b (docker-compose) → 8c → 8b (Dockerfile). Làm 8d cùng lúc với điểm 2, vì cả hai đều là file props ở root.

---

## Kế hoạch triển khai đề xuất

Chia thành các commit nhỏ, mỗi commit build và chạy được:

1. **Build infra**: `Directory.Packages.props` (2) + `Directory.Build.props` + `.editorconfig` (8d). Không đổi hành vi.
2. **Error handling**: map `DbUpdateException` 1451/1452/1062 → 409 (4).
3. **CancellationToken** cho 3 service và controller hiện có (5).
4. **TimeProvider** + setting múi giờ, thay `DateTime.Today` trong `MemberService` (6).
5. **Migration `AddRoomIdToSettlements`** + index `(room_id, expense_date)`, `(room_id, settled_at)` (3, 7). Cập nhật CLAUDE.md.
6. **Health checks** (8a) + **docker-compose MySQL** (8b).
7. **GitHub Actions CI** (8c), sau khi có project test.
8. Hạ tầng phân trang (`PagedResponse<T>`) (7): làm cùng Expenses API.

Mỗi bước xong sẽ cập nhật mục Progress và các quy ước mới (CancellationToken, TimeProvider, phân trang, CPM) trong CLAUDE.md.
