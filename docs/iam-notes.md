# AWS IAM: ghi chép cho người mới

Tài liệu học tập, viết theo góc nhìn một DevOps engineer giải thích cho người mới vào AWS. Nội dung: user, group, role, policy; cấu trúc một policy (`Effect`, `Action`, `Resource`, `Condition`); cách AWS quyết định cho phép hay từ chối; tự dựng profile CLI `roomsplit`; và ví dụ policy **chỉ cho đọc object trong một bucket S3 cụ thể**.

> ⚠️ Các lệnh `aws iam ...` / `aws s3 ...` trong bài **chưa được chạy** trên tài khoản AWS nào (tạo IAM user/role là thay đổi thật trên tài khoản, bạn nên tự chạy và đọc output). Trên máy dev hiện có AWS CLI `2.36.42` và một profile `default` (region `us-east-1`), chưa có profile `roomsplit`.
>
> Account ID trong bài là số giả `111122223333` (số mẫu AWS dùng trong tài liệu chính thức). Thay bằng account ID thật của bạn.

## Mục lục
1. [Bức tranh tổng thể](#1-bức-tranh-tổng-thể)
2. [User, Group, Role, Policy](#2-user-group-role-policy)
3. [Các loại policy](#3-các-loại-policy)
4. [Cấu trúc một policy: Effect, Action, Resource, Condition](#4-cấu-trúc-một-policy-effect-action-resource-condition)
5. [AWS đánh giá quyền như thế nào](#5-aws-đánh-giá-quyền-như-thế-nào)
6. [Ví dụ: chỉ cho đọc object trong một bucket S3](#6-ví-dụ-chỉ-cho-đọc-object-trong-một-bucket-s3)
7. [Tự thiết lập profile `roomsplit`](#7-tự-thiết-lập-profile-roomsplit)
8. [Kiểm tra và gỡ lỗi quyền](#8-kiểm-tra-và-gỡ-lỗi-quyền)
9. [Liên hệ với RoomSplit](#9-liên-hệ-với-roomsplit)
10. [Nguyên tắc vận hành và lỗi hay gặp](#10-nguyên-tắc-vận-hành-và-lỗi-hay-gặp)
11. [Bảng tra cứu nhanh](#11-bảng-tra-cứu-nhanh)

---

## 1. Bức tranh tổng thể

**IAM** (Identity and Access Management) là dịch vụ trả lời hai câu hỏi cho **mọi** request gửi tới AWS:

| Câu hỏi | Thuật ngữ | Ví dụ |
|---|---|---|
| **Bạn là ai?** | Authentication (xác thực) | Request này ký bằng access key của user `roomsplit-cli` |
| **Bạn được làm gì?** | Authorization (phân quyền) | User đó có được `s3:GetObject` trên `receipts/1.jpg` không? |

Mọi thao tác trên AWS (bấm nút trên Console, chạy `aws s3 ls`, code dùng AWS SDK) cuối cùng đều là một **API call** có dạng:

```
Principal (ai)  +  Action (làm gì)  +  Resource (trên cái gì)  +  Context (trong hoàn cảnh nào)
```

IAM lấy 4 thứ đó, so với các **policy** liên quan, rồi trả về **Allow** hoặc **Deny**. Toàn bộ bài này xoay quanh câu đó.

Ví von theo chính bài toán phòng trọ của RoomSplit:

| AWS | Phòng trọ |
|---|---|
| AWS account | Cả dãy trọ |
| Root user | Chủ nhà: có chìa khóa vạn năng, **cất két, không dùng hằng ngày** |
| IAM user | Một người thuê có chìa khóa riêng, dùng lâu dài |
| IAM group | "Các phòng tầng 2": gán quyền chung cho nhiều người một lần |
| IAM role | Thẻ ra vào tạm thời của thợ sửa điện: ai được phép thì mượn, hết giờ tự vô hiệu |
| Policy | Tờ nội quy ghi rõ ai được mở cửa nào, lúc nào |

### Root user

Email dùng để đăng ký tài khoản AWS chính là **root user**. Root có toàn quyền và **không thể bị giới hạn bằng IAM policy**. Việc đầu tiên một DevOps làm với tài khoản mới:

1. Bật MFA cho root.
2. Không tạo access key cho root (nếu có thì xóa).
3. Tạo một danh tính quản trị riêng (IAM user admin hoặc IAM Identity Center) và **từ đó trở đi chỉ dùng danh tính này**.

---

## 2. User, Group, Role, Policy

### 2.1 IAM User

Một danh tính **lâu dài** đại diện cho một người hoặc một ứng dụng. User có thể có:

- **Mật khẩu** để đăng nhập Console.
- **Access key** (`AccessKeyId` + `SecretAccessKey`) để gọi API từ CLI/SDK. Đây là **credential dài hạn**: không tự hết hạn, nếu lộ thì kẻ khác dùng được đến khi bạn xóa.

Một user mới tạo **không có quyền gì cả**. Quyền đến từ policy gắn vào user, hoặc vào group chứa user.

### 2.2 IAM Group

Một **tập hợp user**, chỉ để gán policy cho nhiều user cùng lúc. Group:

- **Không phải** danh tính: không đăng nhập được, không thể ghi tên group làm `Principal` trong policy.
- Không lồng nhau (group không chứa group).
- Một user thuộc tối đa 10 group.

Quy tắc thực tế: **gán policy vào group, không gán thẳng vào user**. Khi có người mới chỉ cần `add-user-to-group`; khi người đó nghỉ, xóa khỏi group là mất quyền.

### 2.3 IAM Role

Một danh tính **không có mật khẩu, không có access key cố định**. Thay vào đó, ai đó được phép sẽ **"đảm nhận" (assume)** role và nhận về **credential tạm thời** (thường 15 phút đến 12 giờ) từ dịch vụ **STS** (Security Token Service).

Role có **hai** loại policy, đây là điểm người mới hay nhầm:

| Policy của role | Trả lời câu hỏi | Ví dụ |
|---|---|---|
| **Trust policy** (assume role policy) | **Ai** được phép assume role này? | User `roomsplit-cli`; dịch vụ EC2; GitHub Actions qua OIDC |
| **Permission policy** | Assume xong thì **được làm gì**? | Đọc object trong bucket `roomsplit-receipts-dev` |

Role được dùng khi:

- **Dịch vụ AWS** cần quyền: EC2 (instance profile), ECS task role, Lambda execution role. Code chạy trên đó lấy credential tự động, **không cần nhét access key vào cấu hình**.
- **Con người** cần quyền cao hơn trong thời gian ngắn: user thường ngày chỉ đọc, khi deploy thì assume role deploy.
- **Truy cập chéo tài khoản** (account A cho account B vào).
- **Danh tính bên ngoài** (federation): đăng nhập SSO, GitHub Actions OIDC… không cần tạo IAM user.

### 2.4 Policy

Một **văn bản JSON** mô tả quyền. Policy tự nó không làm gì; nó có tác dụng khi được **gắn** vào một danh tính (user/group/role) hoặc một tài nguyên (bucket S3, KMS key…).

### 2.5 So sánh nhanh

| | User | Group | Role |
|---|---|---|---|
| Là danh tính (principal)? | Có | **Không** | Có |
| Credential | Mật khẩu, access key **dài hạn** | Không có | **Tạm thời** (STS) |
| Gắn policy được? | Có | Có | Có (+ trust policy) |
| Dùng cho | Người/ứng dụng ngoài AWS (khi chưa có cách tốt hơn) | Gom quyền cho nhiều user | Dịch vụ AWS, nâng quyền tạm thời, chéo tài khoản, SSO |

> 💡 Xu hướng hiện nay (và cũng là khuyến nghị của AWS): **càng ít IAM user có access key càng tốt**. Con người đăng nhập qua IAM Identity Center (SSO), ứng dụng chạy trên AWS dùng role, CI dùng OIDC. IAM user + access key vẫn phổ biến khi học và cho máy dev cá nhân, nên bài này vẫn dùng, nhưng kết hợp với role (mục 7).

---

## 3. Các loại policy

| Loại | Gắn vào | Có `Principal`? | Vai trò |
|---|---|---|---|
| **Identity-based** | User, group, role | Không (principal chính là thứ được gắn) | Cấp quyền cho danh tính |
| **Resource-based** | Tài nguyên: bucket S3, SQS queue, KMS key, **trust policy của role** | **Có** | Tài nguyên tự nói "ai được đụng vào tôi" |
| **Permissions boundary** | User, role | Không | **Trần** quyền tối đa; không tự cấp quyền |
| **SCP** (AWS Organizations) | Account / OU | Không | Trần quyền cho cả tài khoản |
| **Session policy** | Truyền vào lúc assume role | Không | Thu hẹp quyền cho riêng phiên đó |

Identity-based lại chia ba kiểu:

| Kiểu | Đặc điểm | Khi nào dùng |
|---|---|---|
| **AWS managed** | AWS viết sẵn, ví dụ `AmazonS3ReadOnlyAccess`, `AdministratorAccess` | Học, thử nhanh. Thường **quá rộng** (`AmazonS3ReadOnlyAccess` cho đọc **mọi** bucket) |
| **Customer managed** | Bạn tự viết, có ARN riêng, gắn được vào nhiều danh tính, có versioning | **Mặc định nên dùng** cho quyền tùy chỉnh |
| **Inline** | Nhúng thẳng vào một user/group/role, xóa danh tính là mất theo | Quyền gắn chặt 1–1, ví dụ quyền assume role của một group |

---

## 4. Cấu trúc một policy: Effect, Action, Resource, Condition

### 4.1 Khung tổng quát

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ReadReceipts",
      "Effect": "Allow",
      "Action": ["s3:GetObject"],
      "Resource": ["arn:aws:s3:::roomsplit-receipts-dev/*"],
      "Condition": {
        "Bool": { "aws:SecureTransport": "true" }
      }
    }
  ]
}
```

| Phần tử | Bắt buộc? | Ý nghĩa |
|---|---|---|
| `Version` | Nên có | **Luôn** là `"2012-10-17"`. Đây là phiên bản ngôn ngữ policy, không phải ngày bạn viết. Bỏ đi thì AWS dùng bản cũ `2008-10-17`, không hỗ trợ biến `${...}` |
| `Statement` | Có | Mảng các câu lệnh; mỗi câu đánh giá độc lập |
| `Sid` | Không | Tên câu lệnh, chỉ để đọc và debug |
| `Effect` | Có | `Allow` hoặc `Deny` |
| `Action` | Có | Thao tác API áp dụng |
| `Resource` | Có (identity-based) | Tài nguyên áp dụng, viết bằng ARN |
| `Condition` | Không | Điều kiện bổ sung về ngữ cảnh request |
| `Principal` | Chỉ resource-based | Ai được áp dụng (user, role, account, service) |

Đọc một statement như một câu tiếng Việt:

> **[Effect]** việc **[Action]** trên **[Resource]**, **nếu [Condition]**.
>
> "**Cho phép** việc **đọc object** trên **mọi object của bucket roomsplit-receipts-dev**, **nếu** request đi qua HTTPS."

### 4.2 Effect

- `Allow`: cho phép.
- `Deny`: cấm **tuyệt đối**. Một `Deny` khớp sẽ thắng mọi `Allow`, ở bất kỳ policy nào (xem mục 5).

Không có policy nào nói gì → **mặc định là từ chối** (implicit deny). Vì vậy hầu hết policy chỉ cần `Allow`; `Deny` dùng làm **hàng rào** ("dù ai cho phép gì đi nữa, cũng không được…").

### 4.3 Action

Dạng `<tiền tố dịch vụ>:<tên API>`, không phân biệt hoa thường:

```
s3:GetObject          đọc nội dung object (cũng dùng cho HeadObject)
s3:PutObject          ghi object
s3:ListBucket         liệt kê object trong bucket (API ListObjectsV2)
s3:ListAllMyBuckets   liệt kê các bucket của tài khoản
iam:CreateUser
sts:AssumeRole
ec2:DescribeInstances
```

Wildcard: `s3:Get*` (mọi action bắt đầu bằng Get), `s3:*` (mọi action S3), `*` (mọi thứ trên AWS, **tránh**).

> ⚠️ `s3:Get*` nghe như "chỉ đọc" nhưng gồm cả `s3:GetBucketPolicy`, `s3:GetBucketAcl`, `s3:GetObjectAcl`… tức là đọc được cả **cấu hình bảo mật** của bucket. Liệt kê action cụ thể vẫn an toàn hơn.

Tên action **không phải lúc nào cũng trùng tên API**. Ví dụ API `ListObjectsV2` cần quyền `s3:ListBucket`, `HeadObject` cần `s3:GetObject`. Tra bảng "Actions, resources, and condition keys" của từng dịch vụ trong *Service Authorization Reference* của AWS.

### 4.4 Resource và ARN

Tài nguyên được chỉ định bằng **ARN** (Amazon Resource Name):

```
arn:partition:service:region:account-id:resource
arn:aws:iam::111122223333:user/roomsplit-cli
arn:aws:iam::111122223333:role/roomsplit-s3-read
arn:aws:ec2:ap-southeast-1:111122223333:instance/i-0abc123
arn:aws:s3:::roomsplit-receipts-dev              ← bucket
arn:aws:s3:::roomsplit-receipts-dev/*            ← mọi object trong bucket
arn:aws:s3:::roomsplit-receipts-dev/receipts/*   ← object có prefix receipts/
```

Lưu ý ARN của S3 **không có region và account ID** (tên bucket là duy nhất toàn cầu); IAM không có region (IAM là dịch vụ toàn cầu).

**Bẫy kinh điển của S3**: bucket và object là **hai loại tài nguyên khác nhau**:

| Action | Áp dụng lên | ARN đúng |
|---|---|---|
| `s3:ListBucket` | **Bucket** | `arn:aws:s3:::roomsplit-receipts-dev` |
| `s3:GetObject`, `s3:PutObject`, `s3:DeleteObject` | **Object** | `arn:aws:s3:::roomsplit-receipts-dev/*` |

Viết `s3:GetObject` với ARN bucket (không có `/*`) → không khớp object nào → `AccessDenied`. Viết `s3:ListBucket` với ARN `/*` → không khớp bucket → `AccessDenied`. Lỗi này gặp nhiều đến mức nên thuộc lòng.

Một số action không gắn với tài nguyên cụ thể (ví dụ `s3:ListAllMyBuckets`, `ec2:DescribeInstances`) thì phải dùng `"Resource": "*"`.

### 4.5 Condition

Cú pháp: `"Condition": { "<Toán tử>": { "<condition key>": <giá trị> } }`

```json
"Condition": {
  "StringEquals": { "aws:RequestedRegion": "ap-southeast-1" },
  "IpAddress":    { "aws:SourceIp": ["203.0.113.0/24", "198.51.100.10/32"] },
  "Bool":         { "aws:SecureTransport": "true" }
}
```

**Logic kết hợp**, phần dễ đọc nhầm nhất:

| Trong Condition | Kết hợp | Ở ví dụ trên |
|---|---|---|
| Nhiều **toán tử** | **AND** | Đúng region **và** đúng IP **và** HTTPS |
| Nhiều **key** trong cùng toán tử | **AND** | |
| Nhiều **giá trị** của một key (mảng) | **OR** | IP thuộc `203.0.113.0/24` **hoặc** là `198.51.100.10` |

Toán tử hay dùng:

| Nhóm | Toán tử | Ví dụ |
|---|---|---|
| Chuỗi | `StringEquals`, `StringNotEquals`, `StringLike` (có `*`, `?`) | `"s3:prefix": "receipts/*"` |
| Số | `NumericLessThan`, `NumericGreaterThanEquals`… | `"s3:max-keys": "100"` |
| Ngày | `DateGreaterThan`, `DateLessThan` | Quyền tạm có hạn: `"aws:CurrentTime": "2026-12-31T23:59:59Z"` |
| Boolean | `Bool` | `"aws:MultiFactorAuthPresent": "true"` |
| IP | `IpAddress`, `NotIpAddress` | `"aws:SourceIp": "203.0.113.0/24"` |
| ARN | `ArnLike`, `ArnEquals` | `"aws:SourceArn": "arn:aws:..."` |
| Tồn tại | `Null` | `"aws:TokenIssueTime": "false"` (key có tồn tại) |

Hậu tố `...IfExists` (ví dụ `StringEqualsIfExists`): nếu request không có key đó thì coi như điều kiện đúng. Tiền tố `ForAnyValue:` / `ForAllValues:` dùng khi key có nhiều giá trị (ví dụ danh sách tag).

Condition key hay dùng:

| Key | Ý nghĩa |
|---|---|
| `aws:SecureTransport` | Request đi qua HTTPS |
| `aws:SourceIp` | IP public của người gọi |
| `aws:MultiFactorAuthPresent` | Phiên đăng nhập có MFA |
| `aws:RequestedRegion` | Region request nhắm tới |
| `aws:PrincipalTag/<tag>` | Tag gắn trên user/role đang gọi |
| `aws:ResourceTag/<tag>` | Tag gắn trên tài nguyên bị gọi |
| `s3:prefix` | Prefix truyền vào khi `ListBucket` |
| `s3:ExistingObjectTag/<tag>` | Tag của object đang bị đọc |

Ngoài ra policy hỗ trợ **biến**, ví dụ `${aws:username}`: `"Resource": "arn:aws:s3:::roomsplit-home/${aws:username}/*"` cho mỗi user một "thư mục" riêng chỉ bằng một policy.

### 4.6 Phần tử phủ định: NotAction, NotResource, NotPrincipal

`NotAction` = "mọi action **trừ** những action này". Rất mạnh nhưng dễ cấp nhầm quá rộng khi đi với `Allow`. Người mới: chỉ dùng `NotAction` đi với `Deny` (ví dụ "cấm mọi thứ trừ IAM khi chưa có MFA"), còn lại tránh.

---

## 5. AWS đánh giá quyền như thế nào

Rút gọn cho trường hợp **cùng một tài khoản**:

```
                Request đến
                     │
     Có statement Deny nào khớp không? ──── Có ──→ ❌ DENY (explicit deny, không gì cứu được)
                     │ Không
                     ▼
     SCP / permissions boundary / session policy (nếu có)
     có Allow action này không?        ──── Không ─→ ❌ DENY
                     │ Có (hoặc không áp dụng)
                     ▼
     Identity policy HOẶC resource policy
     có Allow khớp không?              ──── Không ─→ ❌ DENY (implicit deny, mặc định)
                     │ Có
                     ▼
                  ✅ ALLOW
```

Ba điều cần nhớ:

1. **Mặc định là từ chối.** Không ai nói gì = không được.
2. **Deny tường minh luôn thắng.** Dù 10 policy cho phép, 1 `Deny` khớp là hết.
3. **Boundary/SCP chỉ là trần, không cấp quyền.** Quyền thực tế = **giao** của (identity policy) với (boundary) với (SCP).

Truy cập **chéo tài khoản** khắt khe hơn: cần **cả hai phía** cho phép (identity policy bên gọi **và** resource policy/trust policy bên bị gọi).

---

## 6. Ví dụ: chỉ cho đọc object trong một bucket S3

Bối cảnh giả định cho RoomSplit: sau này cho phép đính kèm **ảnh hóa đơn** (hóa đơn điện, nước) vào expense, lưu trong bucket `roomsplit-receipts-dev`. Một số danh tính (ví dụ công cụ báo cáo, hoặc người review) chỉ được **xem** ảnh, không được sửa, xóa hay đọc bucket khác.

> Tên bucket là duy nhất **toàn cầu** (giữa mọi tài khoản AWS). Thực tế hay thêm account ID hoặc chuỗi ngẫu nhiên vào tên, ví dụ `roomsplit-receipts-dev-111122223333`. Bài viết tắt là `roomsplit-receipts-dev` cho dễ đọc.

### 6.1 Bản tối thiểu: chỉ `GetObject`

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ReadObjectsInReceiptsBucket",
      "Effect": "Allow",
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::roomsplit-receipts-dev/*"
    }
  ]
}
```

Được: `aws s3 cp s3://roomsplit-receipts-dev/receipts/2026-10/elec.jpg .` (biết trước key).
Không được: `aws s3 ls s3://roomsplit-receipts-dev/` (cần `ListBucket`), ghi, xóa, đọc bucket khác.

Hiệu ứng phụ đáng biết: khi **không có** `s3:ListBucket`, đọc một key không tồn tại sẽ trả về **403 AccessDenied** thay vì 404 NoSuchKey (S3 không tiết lộ object có tồn tại hay không cho người không được liệt kê). Lúc debug thấy 403 mà quyền nhìn có vẻ đúng, hãy kiểm tra lại tên key.

### 6.2 Bản thực tế: đọc + liệt kê (khuyên dùng)

Hầu hết công cụ (CLI `aws s3 ls`, `aws s3 sync`, Console) cần liệt kê trước khi đọc:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ListReceiptsBucket",
      "Effect": "Allow",
      "Action": "s3:ListBucket",
      "Resource": "arn:aws:s3:::roomsplit-receipts-dev"
    },
    {
      "Sid": "ReadObjectsInReceiptsBucket",
      "Effect": "Allow",
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::roomsplit-receipts-dev/*"
    }
  ]
}
```

Hai statement, hai ARN khác nhau: chính là bẫy ở [mục 4.4](#44-resource-và-arn).

### 6.3 Thu hẹp theo prefix (chỉ "thư mục" `receipts/`)

S3 không có thư mục thật; "thư mục" chỉ là **prefix** của key. Muốn chỉ cho đọc `receipts/`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ListOnlyReceiptsPrefix",
      "Effect": "Allow",
      "Action": "s3:ListBucket",
      "Resource": "arn:aws:s3:::roomsplit-receipts-dev",
      "Condition": {
        "StringLike": { "s3:prefix": ["receipts/", "receipts/*"] }
      }
    },
    {
      "Sid": "ReadOnlyReceiptsPrefix",
      "Effect": "Allow",
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::roomsplit-receipts-dev/receipts/*"
    }
  ]
}
```

- Với `GetObject`, prefix nằm **trong ARN**.
- Với `ListBucket`, ARN luôn là bucket, nên prefix phải đặt trong **Condition** `s3:prefix`. Hệ quả: `aws s3 ls s3://roomsplit-receipts-dev/` (prefix rỗng) bị từ chối, phải gọi `aws s3 ls s3://roomsplit-receipts-dev/receipts/`.

### 6.4 Thêm hàng rào: bắt buộc HTTPS

```json
{
  "Sid": "DenyInsecureTransport",
  "Effect": "Deny",
  "Action": "s3:*",
  "Resource": [
    "arn:aws:s3:::roomsplit-receipts-dev",
    "arn:aws:s3:::roomsplit-receipts-dev/*"
  ],
  "Condition": { "Bool": { "aws:SecureTransport": "false" } }
}
```

Thêm statement này vào mảng `Statement` ở trên. Thực tế statement này thường đặt trong **bucket policy** (mục 6.6) để áp cho **mọi** người, không chỉ một danh tính.

### 6.5 Nếu bucket mã hóa bằng KMS key riêng

Nếu bucket dùng SSE-KMS với **customer managed key**, đọc object cần thêm quyền giải mã:

```json
{
  "Sid": "DecryptReceiptsKey",
  "Effect": "Allow",
  "Action": "kms:Decrypt",
  "Resource": "arn:aws:kms:ap-southeast-1:111122223333:key/<key-id>"
}
```

Mặc định S3 mã hóa bằng SSE-S3 (key do S3 quản lý) thì **không cần** statement này. Nếu bucket bật versioning và muốn đọc phiên bản cũ thì thêm `s3:GetObjectVersion`.

### 6.6 Cùng ý đó nhưng viết dạng bucket policy (resource-based)

Thay vì gắn vào danh tính, gắn vào **bucket**, và phải có `Principal`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "AllowRoleToReadReceipts",
      "Effect": "Allow",
      "Principal": { "AWS": "arn:aws:iam::111122223333:role/roomsplit-s3-read" },
      "Action": "s3:GetObject",
      "Resource": "arn:aws:s3:::roomsplit-receipts-dev/*"
    },
    {
      "Sid": "DenyInsecureTransport",
      "Effect": "Deny",
      "Principal": "*",
      "Action": "s3:*",
      "Resource": [
        "arn:aws:s3:::roomsplit-receipts-dev",
        "arn:aws:s3:::roomsplit-receipts-dev/*"
      ],
      "Condition": { "Bool": { "aws:SecureTransport": "false" } }
    }
  ]
}
```

Chọn cách nào?

| | Identity policy (gắn vào role/user) | Bucket policy (gắn vào bucket) |
|---|---|---|
| Câu hỏi trả lời | "Danh tính này được đụng vào đâu?" | "Ai được đụng vào bucket này?" |
| Cùng tài khoản | **Đủ một phía** | Đủ một phía |
| Chéo tài khoản | Cần **cả hai** | Cần **cả hai** |
| Hợp với | Quản lý quyền theo người/ứng dụng | Hàng rào chung cho bucket (HTTPS, chặn public, chỉ cho VPC nào…) |

Khuyến nghị: **cấp quyền** bằng identity policy (mục 7 làm vậy), dùng bucket policy cho **hàng rào** (`Deny`). Và luôn để **Block Public Access** bật (mặc định với bucket mới).

---

## 7. Tự thiết lập profile `roomsplit`

### 7.1 Thiết kế

Thay vì cho user một access key có sẵn quyền S3, ta làm theo cách DevOps hay làm:

```
 Máy dev (~/.aws)                        Tài khoản AWS 111122223333
 ─────────────────                        ─────────────────────────────────────────────
 profile roomsplit          ── ký bằng ──→ IAM user  roomsplit-cli
   (access key dài hạn)                      └─ thuộc group roomsplit-dev
                                                  └─ inline policy: chỉ được sts:AssumeRole
                                                       trên role/roomsplit-*
                                                              │
 profile roomsplit-s3-read  ── AssumeRole ─────────────────────┘
   (CLI tự đổi lấy                          IAM role roomsplit-s3-read
    credential tạm 1 giờ)                      ├─ trust policy: tin user roomsplit-cli
                                               └─ customer managed policy roomsplit-s3-read
                                                    (đọc object bucket roomsplit-receipts-dev, mục 6.2)
```

Vì sao không gắn thẳng quyền S3 vào user?

- Access key dài hạn của `roomsplit-cli` **tự nó gần như vô dụng**: chỉ đổi được lấy credential tạm. Lộ key thì thiệt hại nhỏ hơn, và có thể siết thêm (bắt MFA, mục 7.6).
- Quyền thật nằm trên **role**: sau này ECS/EC2 chạy RoomSplit API cũng dùng role, nên học một lần dùng mãi.
- Muốn thêm quyền khác (ví dụ role `roomsplit-deploy`) chỉ cần tạo role mới; group policy dùng `role/roomsplit-*` đã cho phép sẵn.

> Phương án khác AWS khuyến nghị cho con người là **IAM Identity Center** (SSO, `aws configure sso`): không có access key dài hạn nào trên máy. Phù hợp khi có AWS Organizations hoặc nhiều người, nhiều tài khoản. Với một tài khoản cá nhân để học, IAM user + role như dưới đây đơn giản hơn và dạy được nhiều khái niệm hơn.

### 7.2 Chuẩn bị file policy

Tạo một thư mục làm việc ngoài repo (không commit), ví dụ `D:\aws-iam\roomsplit\`, và lưu 3 file JSON sau. Thay `111122223333` bằng account ID thật.

**`group-assume-roles.json`**: quyền của group, chỉ cho assume các role `roomsplit-*`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "AssumeRoomSplitRoles",
      "Effect": "Allow",
      "Action": "sts:AssumeRole",
      "Resource": "arn:aws:iam::111122223333:role/roomsplit-*"
    }
  ]
}
```

**`trust-roomsplit-s3-read.json`**: trust policy của role, chỉ tin user `roomsplit-cli`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "TrustRoomSplitCliUser",
      "Effect": "Allow",
      "Principal": { "AWS": "arn:aws:iam::111122223333:user/roomsplit-cli" },
      "Action": "sts:AssumeRole"
    }
  ]
}
```

**`roomsplit-s3-read.json`**: permission policy của role, chính là policy ở [mục 6.2](#62-bản-thực-tế-đọc--liệt-kê-khuyên-dùng).

### 7.3 Tạo tài nguyên IAM (chạy bằng profile quản trị)

Các lệnh dưới chạy trong PowerShell, bằng một profile **có quyền quản trị IAM** (trên máy này là `default`; nếu `default` là root thì hãy tạo admin user trước, xem mục 1). Đứng trong thư mục chứa 3 file JSON.

```powershell
$Admin   = "default"
$Account = aws sts get-caller-identity --query Account --output text --profile $Admin
$Region  = "ap-southeast-1"   # Singapore: gần Việt Nam nhất
$Bucket  = "roomsplit-receipts-dev"

# 1. Group + quyền assume role (inline policy)
aws iam create-group --group-name roomsplit-dev --profile $Admin
aws iam put-group-policy --group-name roomsplit-dev `
  --policy-name assume-roomsplit-roles `
  --policy-document file://group-assume-roles.json --profile $Admin

# 2. User, cho vào group
aws iam create-user --user-name roomsplit-cli --profile $Admin
aws iam add-user-to-group --group-name roomsplit-dev --user-name roomsplit-cli --profile $Admin

# 3. Role + trust policy, thời hạn phiên tối đa 1 giờ
aws iam create-role --role-name roomsplit-s3-read `
  --assume-role-policy-document file://trust-roomsplit-s3-read.json `
  --max-session-duration 3600 --profile $Admin

# 4. Customer managed policy, gắn vào role
aws iam create-policy --policy-name roomsplit-s3-read `
  --policy-document file://roomsplit-s3-read.json --profile $Admin
aws iam attach-role-policy --role-name roomsplit-s3-read `
  --policy-arn "arn:aws:iam::${Account}:policy/roomsplit-s3-read" --profile $Admin

# 5. Bucket thử nghiệm + 1 object mẫu (bằng quyền admin)
aws s3 mb "s3://$Bucket" --region $Region --profile $Admin
"hello receipt" | Out-File -Encoding utf8 sample.txt
aws s3 cp sample.txt "s3://$Bucket/receipts/sample.txt" --profile $Admin

# 6. Access key cho user (SecretAccessKey chỉ hiện MỘT lần duy nhất)
aws iam create-access-key --user-name roomsplit-cli --profile $Admin
```

> 🔐 Output bước 6 chứa `SecretAccessKey`. Không dán vào chat, không commit, không lưu vào `appsettings*.json`. Nhập thẳng vào bước 7.4 rồi đóng terminal.

### 7.4 Cấu hình profile trên máy

```powershell
aws configure --profile roomsplit
# AWS Access Key ID [None]:      <AccessKeyId ở bước 6>
# AWS Secret Access Key [None]:  <SecretAccessKey ở bước 6>
# Default region name [None]:    ap-southeast-1
# Default output format [None]:  json
```

Sau đó mở `%USERPROFILE%\.aws\config` (ví dụ `notepad $HOME\.aws\config`) và thêm profile dùng role:

```ini
[default]
region = us-east-1
output = json

[profile roomsplit]
region = ap-southeast-1
output = json

[profile roomsplit-s3-read]
role_arn = arn:aws:iam::111122223333:role/roomsplit-s3-read
source_profile = roomsplit
role_session_name = roomsplit-cli
region = ap-southeast-1
```

Còn `%USERPROFILE%\.aws\credentials` (do `aws configure` ghi) sẽ có:

```ini
[roomsplit]
aws_access_key_id = AKIA...
aws_secret_access_key = ...
```

> ⚠️ Bẫy đặt tên: trong file **`config`** viết `[profile roomsplit]`, trong file **`credentials`** viết `[roomsplit]` (không có chữ `profile`). Riêng `default` thì cả hai file đều viết `[default]`.

Profile `roomsplit-s3-read` không chứa secret nào: CLI đọc `source_profile`, gọi `sts:AssumeRole`, rồi cache credential tạm trong `%USERPROFILE%\.aws\cli\cache\` đến khi hết hạn.

### 7.5 Dùng profile

```powershell
# Cách 1: theo từng lệnh
aws s3 ls s3://roomsplit-receipts-dev/ --profile roomsplit-s3-read

# Cách 2: cho cả phiên PowerShell
$env:AWS_PROFILE = "roomsplit-s3-read"
aws s3 ls s3://roomsplit-receipts-dev/
Remove-Item Env:AWS_PROFILE      # trả lại default
```

Kết quả mong đợi:

| Lệnh | Profile | Mong đợi | Vì sao |
|---|---|---|---|
| `aws sts get-caller-identity` | `roomsplit` | `...:user/roomsplit-cli` | Luôn được phép, không cần policy |
| `aws sts get-caller-identity` | `roomsplit-s3-read` | `...:assumed-role/roomsplit-s3-read/roomsplit-cli` | Đã assume role thành công |
| `aws s3 ls s3://roomsplit-receipts-dev/` | `roomsplit` | ❌ AccessDenied | User không có quyền S3 nào |
| `aws s3 ls s3://roomsplit-receipts-dev/ --recursive` | `roomsplit-s3-read` | ✅ thấy `receipts/sample.txt` | `s3:ListBucket` |
| `aws s3 cp s3://roomsplit-receipts-dev/receipts/sample.txt -` | `roomsplit-s3-read` | ✅ in ra `hello receipt` | `s3:GetObject` |
| `aws s3 cp sample.txt s3://roomsplit-receipts-dev/x.txt` | `roomsplit-s3-read` | ❌ AccessDenied | Không có `s3:PutObject` |
| `aws s3 rm s3://roomsplit-receipts-dev/receipts/sample.txt` | `roomsplit-s3-read` | ❌ AccessDenied | Không có `s3:DeleteObject` |
| `aws s3 ls` (liệt kê mọi bucket) | `roomsplit-s3-read` | ❌ AccessDenied | Không có `s3:ListAllMyBuckets` |

Thử cả các dòng ❌: kiểm tra quyền **bị chặn** quan trọng ngang kiểm tra quyền **được cho**.

### 7.6 Nâng cấp tùy chọn: bắt buộc MFA khi assume role

Gán thiết bị MFA (app Authenticator) cho user `roomsplit-cli` trên Console, rồi thêm Condition vào **trust policy**:

```json
"Condition": { "Bool": { "aws:MultiFactorAuthPresent": "true" } }
```

và thêm dòng vào profile:

```ini
[profile roomsplit-s3-read]
mfa_serial = arn:aws:iam::111122223333:mfa/roomsplit-cli
...
```

Từ đó mỗi lần credential tạm hết hạn, CLI hỏi mã 6 số. Lộ access key thôi là chưa đủ để vào.

### 7.7 Dọn dẹp khi học xong

Thứ tự ngược lại (IAM không cho xóa thứ đang được tham chiếu):

```powershell
aws s3 rb "s3://$Bucket" --force --profile $Admin
aws iam detach-role-policy --role-name roomsplit-s3-read --policy-arn "arn:aws:iam::${Account}:policy/roomsplit-s3-read" --profile $Admin
aws iam delete-policy --policy-arn "arn:aws:iam::${Account}:policy/roomsplit-s3-read" --profile $Admin
aws iam delete-role --role-name roomsplit-s3-read --profile $Admin
aws iam list-access-keys --user-name roomsplit-cli --profile $Admin   # lấy AccessKeyId
aws iam delete-access-key --user-name roomsplit-cli --access-key-id <AccessKeyId> --profile $Admin
aws iam remove-user-from-group --group-name roomsplit-dev --user-name roomsplit-cli --profile $Admin
aws iam delete-user --user-name roomsplit-cli --profile $Admin
aws iam delete-group-policy --group-name roomsplit-dev --policy-name assume-roomsplit-roles --profile $Admin
aws iam delete-group --group-name roomsplit-dev --profile $Admin
```

Rồi xóa các mục `roomsplit` trong `~/.aws/config` và `~/.aws/credentials`.

---

## 8. Kiểm tra và gỡ lỗi quyền

| Công cụ | Dùng để | Lệnh / nơi tìm |
|---|---|---|
| `sts get-caller-identity` | "Tôi đang là ai?" Luôn chạy **đầu tiên** khi gặp AccessDenied: rất hay là dùng nhầm profile | `aws sts get-caller-identity --profile roomsplit-s3-read` |
| Thứ tự ưu tiên credential | CLI/SDK lấy credential theo chuỗi: tham số `--profile` → biến môi trường `AWS_ACCESS_KEY_ID`… → `AWS_PROFILE` → profile `default` → role của EC2/ECS | Biến `AWS_ACCESS_KEY_ID` còn sót trong shell sẽ **đè** `AWS_PROFILE` |
| Access Analyzer validate | Bắt lỗi cú pháp, cảnh báo policy quá rộng **trước khi** áp | `aws accessanalyzer validate-policy --policy-type IDENTITY_POLICY --policy-document file://roomsplit-s3-read.json` |
| Policy simulator | Hỏi "role này có được làm X trên Y không?" mà không cần gọi thật | `aws iam simulate-principal-policy --policy-source-arn arn:aws:iam::111122223333:role/roomsplit-s3-read --action-names s3:GetObject s3:PutObject --resource-arns arn:aws:s3:::roomsplit-receipts-dev/receipts/sample.txt` |
| Thông báo lỗi | AWS thường nói rõ ai, action gì, resource nào, và **loại policy** chặn (`...because no identity-based policy allows the s3:PutObject action`) | Đọc kỹ, đừng chỉ nhìn chữ AccessDenied |
| CloudTrail | Nhật ký mọi API call (ai gọi, lúc nào, kết quả); 90 ngày event history miễn phí | Console → CloudTrail → Event history |

Quy trình gỡ lỗi AccessDenied, theo thứ tự:

1. `get-caller-identity`: đúng danh tính chưa?
2. Action đúng tên chưa (`ListBucket` chứ không phải `ListObjects`)?
3. ARN đúng loại chưa (bucket hay `bucket/*`)?
4. Có `Deny` nào khớp không (bucket policy, SCP, boundary)?
5. Condition có thỏa không (prefix, IP, MFA, HTTPS)?
6. Có KMS key nào cần `kms:Decrypt` không?

---

## 9. Liên hệ với RoomSplit

RoomSplit hiện **chưa dùng AWS** (chạy bằng Docker Compose trên máy). Nếu sau này thêm tính năng lưu ảnh hóa đơn lên S3 hoặc deploy lên AWS, các nguyên tắc ở trên áp dụng thế này:

| Môi trường | Ứng dụng lấy credential từ đâu | Tuyệt đối không |
|---|---|---|
| Máy dev | `AWS_PROFILE=roomsplit-s3-read` (hoặc một profile role có quyền ghi riêng cho dev); AWS SDK for .NET tự đọc `~/.aws` | Đặt access key trong `appsettings*.json` hay `.env` commit lên git |
| Docker Compose dev | Mount `~/.aws` read-only vào container + `AWS_PROFILE` | Bake key vào image |
| ECS / EC2 | **Task role** / **instance profile**: SDK tự lấy credential tạm từ metadata | Tạo IAM user cho ứng dụng |
| GitHub Actions | **OIDC** → assume role `roomsplit-deploy` | Lưu access key trong GitHub Secrets |

Code .NET không cần biết credential đến từ đâu: `AWSSDK.S3` dùng **default credential chain** (giống mục 8). Đây cũng là lý do role là trung tâm của IAM: viết policy cho role một lần, dùng được từ máy dev đến production.

Lưu ý stack: các package `AWSSDK.*` không phụ thuộc EF Core, nên không đụng ràng buộc EF Core 9.0.x của dự án; nếu thêm thì pin version trong `Directory.Packages.props` như các package khác.

---

## 10. Nguyên tắc vận hành và lỗi hay gặp

**Nguyên tắc**

1. **Least privilege**: bắt đầu từ không có gì, thêm đúng action, đúng ARN cần thiết. Mở rộng dễ hơn thu hẹp.
2. Không dùng root hằng ngày; bật MFA cho root và mọi user có mật khẩu Console.
3. Ưu tiên credential **tạm thời** (role, SSO, OIDC) hơn access key dài hạn.
4. Gán policy vào **group/role**, không vào từng user.
5. Customer managed policy > inline > AWS managed rộng.
6. Xoay vòng (rotate) access key định kỳ; xóa key không dùng (IAM → Credential report có cột "last used").
7. Đặt tên có tiền tố dự án (`roomsplit-*`) để policy dùng wildcard an toàn và dễ dọn dẹp.
8. Coi policy là code: lưu JSON trong git (repo hạ tầng), review như code. Bước tiếp theo khi đã quen: Terraform / CloudFormation / CDK.

**Lỗi hay gặp**

| Lỗi | Hậu quả | Sửa |
|---|---|---|
| `s3:GetObject` trên ARN bucket (thiếu `/*`) | AccessDenied khi đọc | ARN object: `bucket/*` |
| `s3:ListBucket` trên `bucket/*` | AccessDenied khi `ls` | ARN bucket, không `/*` |
| Gắn `AmazonS3ReadOnlyAccess` cho "nhanh" | Đọc được **mọi** bucket, kể cả bucket chứa backup DB | Viết policy riêng như mục 6 |
| `"Action": "*", "Resource": "*"` | Toàn quyền tài khoản | Không bao giờ, trừ role admin có MFA |
| Quên `Version` | Biến `${aws:username}` không chạy | Luôn `"2012-10-17"` |
| Nhầm `[profile x]` / `[x]` giữa `config` và `credentials` | CLI báo "profile not found" | Xem mục 7.4 |
| Biến môi trường `AWS_ACCESS_KEY_ID` còn sót | Chạy nhầm danh tính | `get-caller-identity`, xóa biến |
| Commit access key lên GitHub | Bot quét và dùng trong vài phút | **Deactivate + xóa key ngay**, xem CloudTrail, rồi mới xóa khỏi git history |
| Trust policy quá rộng (`"Principal": {"AWS": "*"}`) | Ai cũng assume được role | Ghi đúng ARN user/role/account |

---

## 11. Bảng tra cứu nhanh

```powershell
# Danh tính hiện tại
aws sts get-caller-identity --profile roomsplit
aws configure list --profile roomsplit          # credential lấy từ đâu
aws configure list-profiles

# Đọc thông tin IAM (cần quyền quản trị)
aws iam list-users
aws iam list-groups-for-user --user-name roomsplit-cli
aws iam list-attached-role-policies --role-name roomsplit-s3-read
aws iam get-role --role-name roomsplit-s3-read --query Role.AssumeRolePolicyDocument
aws iam get-policy-version --policy-arn arn:aws:iam::111122223333:policy/roomsplit-s3-read --version-id v1

# Cập nhật policy (customer managed giữ tối đa 5 version)
aws iam create-policy-version --policy-arn arn:aws:iam::111122223333:policy/roomsplit-s3-read `
  --policy-document file://roomsplit-s3-read.json --set-as-default

# Kiểm tra policy
aws accessanalyzer validate-policy --policy-type IDENTITY_POLICY --policy-document file://roomsplit-s3-read.json
aws iam simulate-principal-policy --policy-source-arn <role-arn> --action-names s3:GetObject --resource-arns <object-arn>

# Assume role thủ công (để thấy credential tạm trông thế nào)
aws sts assume-role --role-arn arn:aws:iam::111122223333:role/roomsplit-s3-read `
  --role-session-name test --profile roomsplit
```

| Khái niệm | Một câu |
|---|---|
| User | Danh tính lâu dài, có mật khẩu/access key |
| Group | Túi đựng user để gán policy chung; không phải danh tính |
| Role | Danh tính "đi mượn", cấp credential tạm qua STS |
| Trust policy | Ai được mượn role |
| Permission policy | Mượn xong được làm gì |
| Effect | `Allow` / `Deny`; Deny tường minh luôn thắng |
| Action | `service:Api`, ví dụ `s3:GetObject` |
| Resource | ARN; với S3 phân biệt `bucket` và `bucket/*` |
| Condition | Toán tử khác nhau = AND, giá trị trong mảng = OR |
| Mặc định | Không ai cho phép = từ chối |
