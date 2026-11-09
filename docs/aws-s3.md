# Amazon S3 cho RoomSplit

Tài liệu này giải thích Amazon S3 từ cơ bản đến cách áp dụng vào RoomSplit.

## 1. S3 là gì?

Amazon S3 (Simple Storage Service) là dịch vụ lưu trữ **object** trên AWS. S3 phù hợp
cho:

- Ảnh, video, PDF và file đính kèm.
- File backup, CSV, Excel và báo cáo export.
- Log, dữ liệu lớn và static assets.
- File cần tải xuống qua Internet.

S3 **không phải database** và không nên dùng để lưu dữ liệu quan hệ như Room,
Member hoặc Expense.

## 2. Các khái niệm cốt lõi

### Bucket

Bucket là container chứa object. Tên bucket phải duy nhất trên toàn AWS.

Nên tách bucket theo môi trường:

```text
roomsplit-assets-dev
roomsplit-assets-staging
roomsplit-assets-production
```

### Object

File trong S3 được gọi là object:

```text
Key       = rooms/12/expenses/345/receipt.jpg
Content   = nội dung file
Metadata  = thông tin bổ sung
```

S3 không có folder thật. Dấu `/` chỉ là quy ước trong object key.

### Object key

Key là định danh của object trong bucket. Không nên dùng trực tiếp tên file do
người dùng gửi:

```text
receipt.jpg
```

Nên để server tạo key:

```text
rooms/{roomId}/expenses/{expenseId}/{guid}.jpg
```

Ví dụ:

```text
rooms/12/expenses/345/7f1c0c6d-receipt.jpg
```

## 3. S3 khác filesystem thế nào?

| Filesystem | S3 |
|---|---|
| Có thư mục thật | Không có folder thật |
| Có thể sửa một phần file | Thường upload lại toàn object |
| File nằm trên disk/server | AWS quản lý hạ tầng phân tán |
| Cần quản lý disk | Tự mở rộng theo nhu cầu |
| Rename nhanh | Rename thực chất là copy + delete |
| Quyền theo server | Quyền qua IAM và bucket policy |

S3 có độ bền rất cao, nhưng vẫn cần versioning, backup và lifecycle để chống xóa
nhầm và kiểm soát chi phí.

## 4. Các trường hợp dùng phổ biến

### Lưu ảnh biên lai

Database lưu metadata và object key:

```text
Expense
  id = 345
  amount = 300000

ExpenseAttachment
  expense_id = 345
  object_key = rooms/12/expenses/345/abc.jpg
```

### Upload trực tiếp từ frontend

```text
Frontend → API xin presigned URL
API → trả URL tạm thời
Frontend → upload trực tiếp lên S3
Frontend → API xác nhận upload
```

API không phải nhận toàn bộ file nên giảm tải server.

### Export báo cáo

```text
API tạo CSV
API upload CSV lên S3
API trả presigned download URL
```

### Backup

- Backup database lên S3.
- Bật lifecycle chuyển file cũ sang Glacier.
- Bật versioning để khôi phục file bị ghi đè hoặc xóa.

### Static website

S3 có thể chứa HTML, CSS và JavaScript. Với production thường kết hợp thêm
CloudFront.

## 5. Public và private object

### Private bucket — lựa chọn khuyến nghị

Bucket private, người dùng tải file bằng **presigned URL** có thời hạn ngắn,
thường 5 đến 15 phút.

Phù hợp với:

- Biên lai.
- Hóa đơn.
- Backup.
- File chứa thông tin cá nhân.

### Public bucket

Chỉ dùng khi file thực sự công khai, ví dụ static assets hoặc ảnh public. Không
nên public bucket chứa dữ liệu của RoomSplit.

Nên bật:

- S3 Block Public Access.
- Object Ownership: Bucket owner enforced.
- Không dùng ACL `public-read`.

## 6. Presigned URL

Presigned URL là URL tạm thời do backend ký. URL có thể chỉ cho phép upload hoặc
download đúng một object trong khoảng thời gian giới hạn.

Ví dụ workflow:

```text
Client → API tạo upload URL
API → kiểm tra quyền và trả URL cho rooms/12/expenses/345/abc.jpg
Client → PUT file vào URL
Client → API xác nhận upload
```

### Ưu điểm

- Không phải chuyển file qua API.
- API nhẹ hơn.
- S3 xử lý upload lớn tốt.
- Có thể giới hạn thời gian sống và object key.

### Nhược điểm

- URL bị lộ có thể bị dùng trong thời gian còn hiệu lực.
- API phải kiểm soát object key và quyền sở hữu.
- Upload thành công ở S3 chưa đồng nghĩa database đã ghi thành công.

## 7. Bảo mật S3

### IAM

Không nhúng Access Key vào source code, `appsettings.json` hoặc Git.

Trong AWS nên dùng:

```text
EC2/ECS → IAM Role → S3
```

Ví dụ quyền tối thiểu cho API:

```json
{
  "Effect": "Allow",
  "Action": [
    "s3:GetObject",
    "s3:PutObject",
    "s3:DeleteObject"
  ],
  "Resource": "arn:aws:s3:::roomsplit-assets-production/rooms/*"
}
```

### Encryption

- **SSE-S3**: AWS quản lý encryption key, đơn giản và phù hợp MVP.
- **SSE-KMS**: kiểm soát key tốt hơn nhưng phức tạp và có thêm chi phí.

### Bucket policy

Nên từ chối request không dùng HTTPS:

```json
{
  "Effect": "Deny",
  "Principal": "*",
  "Action": "s3:*",
  "Resource": [
    "arn:aws:s3:::roomsplit-assets-production",
    "arn:aws:s3:::roomsplit-assets-production/*"
  ],
  "Condition": {
    "Bool": {
      "aws:SecureTransport": "false"
    }
  }
}
```

### Validate file

Không tin hoàn toàn `Content-Type` do client gửi. API nên kiểm tra:

- Kích thước tối đa.
- Extension.
- MIME type.
- Magic bytes/file signature.
- Tên file.
- Nội dung độc hại nếu hệ thống yêu cầu cao.

Không cho client tự chọn object key tùy ý.

## 8. Storage class

| Storage class | Dùng cho |
|---|---|
| S3 Standard | File truy cập thường xuyên |
| Intelligent-Tiering | Không biết tần suất truy cập |
| Standard-IA | Ít truy cập nhưng cần lấy nhanh |
| Glacier Instant Retrieval | Lưu trữ lâu, đôi lúc cần đọc |
| Glacier Flexible Retrieval | Backup, khôi phục chậm hơn |
| Glacier Deep Archive | Lưu trữ dài hạn, rất ít truy cập |

Định hướng cho RoomSplit:

- Biên lai mới: S3 Standard.
- Biên lai cũ: lifecycle chuyển sang Standard-IA.
- Backup lâu dài: Glacier.

## 9. Versioning và lifecycle

### Versioning

Nếu upload lại cùng key:

```text
rooms/12/expenses/345/receipt.jpg
```

S3 có thể giữ nhiều version. Điều này hữu ích khi cần khôi phục file cũ hoặc
chống xóa nhầm.

Versioning làm tăng chi phí lưu trữ, vì vậy cần lifecycle cho cả các version cũ.

### Lifecycle

Ví dụ:

```text
Sau 365 ngày  → Standard-IA
Sau 730 ngày  → Glacier
Sau 2555 ngày → xóa
```

Nếu bật versioning, cần cấu hình dọn cả non-current versions.

## 10. Multipart upload

File lớn nên dùng multipart upload:

```text
File 100 MB
  ├── Part 1
  ├── Part 2
  ├── Part 3
  └── ...
```

Ưu điểm:

- Upload song song.
- Có thể resume khi mạng lỗi.
- Không phải upload lại toàn bộ file.

Với ảnh biên lai nhỏ, upload thông thường là đủ. Cần cấu hình lifecycle để tự
abort multipart upload bị bỏ dở.

## 11. S3 consistency

S3 hiện hỗ trợ strong read-after-write consistency:

- Upload xong có thể đọc ngay.
- Xóa xong lần đọc tiếp theo thấy object đã xóa.
- List object phản ánh thay đổi theo mô hình nhất quán hiện tại.

Ứng dụng vẫn phải xử lý timeout, lỗi mạng, retry và thao tác lặp an toàn
(idempotency).

## 12. Áp dụng vào RoomSplit

### Hiện trạng

RoomSplit hiện có:

- MySQL cho dữ liệu nghiệp vụ.
- Redis cho cache member.
- Docker, EC2 và AWS lab.
- Chưa có tính năng file upload.
- Chưa có authentication.

Các tài liệu AWS hiện tại đã chuẩn bị S3 Gateway Endpoint:

- [aws-lab-architecture.md](./aws-lab-architecture.md)
- [aws-lab-walkthrough.md](./aws-lab-walkthrough.md)
- [aws-ec2-deploy.md](./aws-ec2-deploy.md)

Gateway Endpoint cho phép workload trong private subnet truy cập S3 qua mạng
AWS mà không cần NAT Gateway.

### Tính năng phù hợp nhất

Khi làm Expense API, thêm attachment cho:

- Ảnh hóa đơn điện.
- Ảnh hóa đơn nước.
- Hóa đơn Internet.
- Ảnh bill ăn uống.

Database nên có bảng `expense_attachments`:

```text
id
expense_id
object_key
original_file_name
content_type
file_size
created_at
```

Không lưu binary file trong MySQL và không lưu presigned URL lâu dài. Chỉ lưu
object key:

```text
rooms/12/expenses/345/abc.jpg
```

## 13. Kiến trúc đề xuất

```text
Client
  │
  │ 1. POST /expenses/{id}/attachments/upload-url
  ▼
RoomSplit API
  │
  │ 2. Kiểm tra room, expense, member và file type
  │ 3. Tạo presigned URL
  ▼
Client ───────────────► S3 private bucket
  │
  │ 4. POST /attachments/{id}/complete
  ▼
RoomSplit API
  │
  └── Ghi metadata vào MySQL
```

Khi tải file:

```text
Client → API xin download URL → S3
```

Redis vẫn chỉ là cache, không thay thế S3.

## 14. Định hướng triển khai trong .NET

Sau khi chốt nghiệp vụ attachment, thêm package `AWSSDK.S3`. Version phải được
đặt trong [Directory.Packages.props](../Directory.Packages.props), không đặt
trực tiếp trong `.csproj`.

Interface đề xuất:

```csharp
public interface IObjectStorage
{
    Task<string> CreateUploadUrlAsync(
        string objectKey,
        string contentType,
        TimeSpan lifetime,
        CancellationToken ct);

    Task<string> CreateDownloadUrlAsync(
        string objectKey,
        TimeSpan lifetime,
        CancellationToken ct);

    Task DeleteAsync(string objectKey, CancellationToken ct);
}
```

Tạo key ở server:

```csharp
var key = $"rooms/{roomId}/expenses/{expenseId}/{Guid.NewGuid():N}.jpg";
```

Theo convention hiện tại của project:

- Controller không gọi S3 trực tiếp.
- Service kiểm tra business rules và quyền truy cập.
- S3 client đăng ký qua Dependency Injection.
- Mọi phương thức async nhận `CancellationToken`.
- Không dùng `catch (Exception)` để nuốt lỗi.

## 15. Quyết định nên chọn cho RoomSplit

- Một bucket cho mỗi environment.
- Bucket private.
- Presigned URL.
- Object key do server sinh.
- Database lưu object key.
- S3 Standard ban đầu.
- SSE-S3.
- IAM Role trên EC2.
- S3 Gateway Endpoint.
- Giới hạn file, ví dụ 10 MB.
- Chỉ cho phép JPEG, PNG và PDF.

Không nên:

- Public bucket cho biên lai.
- Commit AWS key vào Git.
- Lưu presigned URL vào database.
- Cho client tự truyền object key.
- Lưu file trong MySQL.
- Dùng S3 làm cache.
- Để API đọc toàn bộ file lớn vào memory.
- Upload trước khi kiểm tra expense và room.

## 16. Các lỗi thường gặp

1. **Bucket mở public ngoài ý muốn**: bật Block Public Access và kiểm tra bucket
   policy.
2. **Lưu Access Key trong `appsettings.json`**: dùng IAM Role hoặc user-secrets
   cho local.
3. **Lưu URL thay vì key**: presigned URL sẽ hết hạn.
4. **Không giới hạn file size**: có thể gây tốn tiền hoặc làm API quá tải.
5. **Không dọn multipart upload thất bại**: cấu hình lifecycle abort incomplete
   multipart uploads.
6. **Xóa database nhưng không xóa object**: tạo orphan files; cần cleanup job.
7. **Xóa S3 trước rồi database thất bại**: cần retry và workflow idempotent vì S3
   và MySQL không dùng chung transaction.
8. **Dùng bucket chung cho dev và production**: dễ xóa nhầm dữ liệu thật.

## 17. Lộ trình triển khai

1. Hoàn thành Expense API.
2. Chốt nghiệp vụ attachment.
3. Viết tài liệu thiết kế riêng trước khi code.
4. Tạo bucket dev private.
5. Thêm `AWSSDK.S3`.
6. Thêm `IObjectStorage` và `S3ObjectStorage`.
7. Thêm bảng `expense_attachments`.
8. Thêm API tạo upload URL.
9. Thêm API xác nhận upload và download.
10. Thêm lifecycle, logging, metrics và cleanup job.
11. Sau khi có authentication mới mở upload cho người dùng thật.

Nguyên tắc quan trọng:

> S3 là nơi lưu file, MySQL là nơi lưu quan hệ và metadata, Redis là cache.
> Ba hệ thống cần giữ đúng vai trò riêng.
