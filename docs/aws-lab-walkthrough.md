# AWS lab: hướng dẫn từng bước trên Console

Thực hiện kiến trúc đã chốt trong [aws-lab-architecture.md](aws-lab-architecture.md) (sơ đồ: [aws-lab-architecture.html](aws-lab-architecture.html)). Theo phương án **T1**: bấm trên Console để nhìn thấy từng khối, sau mỗi bước dùng CLI để kiểm tra.

Bài này gồm **giai đoạn 0 (chuẩn bị)** và **giai đoạn 1 (dựng mạng)**, hai giai đoạn này không tốn tiền, cùng **giai đoạn 2 (deploy RoomSplit lên EC2)**, có tính tiền theo giờ khi máy chạy. Giai đoạn 3–4 (NAT, NACL) viết sau.

## Mục lục
- [Tình trạng tài khoản (đã kiểm tra 28/10/2026)](#tình-trạng-tài-khoản-đã-kiểm-tra-28102026)
- [Giai đoạn 0: Chuẩn bị](#giai-đoạn-0-chuẩn-bị)
- [Giai đoạn 1: Dựng mạng](#giai-đoạn-1-dựng-mạng)
  - [1.1 VPC](#11-vpc)
  - [1.2 Subnet](#12-subnet)
  - [1.3 Internet Gateway](#13-internet-gateway)
  - [1.4 Route table](#14-route-table)
  - [1.5 Gateway Endpoint S3](#15-gateway-endpoint-s3)
  - [1.6 Security Group](#16-security-group)
  - [1.7 Network ACL cho tầng data](#17-network-acl-cho-tầng-data)
  - [1.8 Kiểm tra tổng thể](#18-kiểm-tra-tổng-thể)
- [Giai đoạn 2: Deploy RoomSplit lên EC2](#giai-đoạn-2-deploy-roomsplit-lên-ec2)
  - [2.1 Cấp quyền EICE (root)](#21-cấp-quyền-eice-đăng-nhập-root-làm-một-lần)
  - [2.4 Launch instance](#24-launch-instance)
  - [2.6 Build image và chuẩn bị file](#26-build-image-và-chuẩn-bị-file-máy-dev)
  - [2.8 Chạy RoomSplit trên EC2](#28-chạy-roomsplit-trên-ec2)
  - [2.9 Truy cập từ Internet](#29-truy-cập-từ-internet)
- [Lỗi hay gặp](#lỗi-hay-gặp)

---

## Tình trạng tài khoản (đã kiểm tra 28/10/2026)

| Mục | Kết quả | Ghi chú |
|---|---|---|
| Loại tài khoản | Tạo sau 15/07/2025 → **Free plan** (credit, 6 tháng) | Mục 2 của bài kiến trúc |
| MFA cho root | **Đã bật** | `aws iam get-account-summary` → `AccountMFAEnabled = 1` |
| CLI profile `default` | IAM user `dev-readonly`, region `us-east-1` | Tên có chữ "readonly" nhưng group `developers` của user này có `AmazonEC2FullAccess`, `AmazonRDSFullAccess`, `AmazonS3FullAccess`… nên **tạo được VPC**. Nên đổi tên user cho đúng thực tế (không gấp) |
| Quyền Budgets của `dev-readonly` | **Không có** | Budgets phải làm bằng root (bước 0.2) |
| VPC hiện có | Chỉ có default VPC `172.31.0.0/16` | Giữ nguyên, không xóa |
| AZ dùng cho lab | `us-east-1a` = `use1-az1`, `us-east-1b` = `use1-az2` | Đã ghi vào mục 8 của bài kiến trúc |

---

## Giai đoạn 0: Chuẩn bị

### 0.1 Xem credit và hạn của Free plan (đăng nhập root)

Vào **Billing and Cost Management**, xem các mục **Credits** và **Free Tier**:

- Số credit còn lại và **ngày hết hạn** của Free plan (6 tháng kể từ ngày tạo tài khoản).
- Hết credit hoặc hết hạn thì tài khoản bị **đóng** nếu không nâng cấp lên Paid plan. Free plan **không trừ tiền vào thẻ**, đó là "lưới an toàn" của loại tài khoản này.
- Trang chủ Console (hoặc trong Billing) có danh sách **hoạt động khám phá** để nhận thêm credit, ví dụ tạo budget, chạy một EC2 instance. Hai việc này nằm sẵn trong lộ trình lab nên làm đúng cách là nhận được credit.

> Ghi ngày hết hạn vào lịch. Không ghi số tài khoản (Account ID) vào repo.

### 0.2 Tạo AWS Budget (root)

Credit chỉ là tiền trả trước: NAT Gateway quên xóa vẫn **ăn hết credit** trong vài tuần. Budget giúp phát hiện sớm.

1. **Billing and Cost Management → Budgets → Create budget**.
2. Chọn **Customize (advanced)** → **Cost budget** → Next.
3. Budget name: `roomsplit-lab-monthly`. Period: **Monthly**. Budget amount: **5** USD.
4. Phần **Advanced options**: nếu có ô **Credits**, **bỏ chọn**. Như vậy budget đo mức tiêu thụ **trước khi trừ credit**, tức là đúng tốc độ credit đang bị tiêu.
5. Thêm 2 alert, gửi về email của bạn:
   - **Actual** cost > **80%** (4 USD).
   - **Forecasted** cost > **100%** (5 USD).
6. Create.

Nếu thấy giao diện khác (AWS hay đổi Console), mục tiêu vẫn là: **budget theo tháng, 5 USD, có cảnh báo qua email**.

### 0.3 Đăng nhập Console bằng IAM user, không dùng root

Root chỉ dùng cho việc chỉ root làm được (billing, budget, đóng tài khoản). Giai đoạn 1 nên làm bằng IAM user `dev-readonly`:

- Link đăng nhập IAM user nằm ở **IAM → Dashboard → AWS Account → Sign-in URL for IAM users**.
- Nếu user chưa có mật khẩu Console: dùng root vào **IAM → Users → dev-readonly → Security credentials → Enable console access**.

### 0.4 Chọn region

Góc trên bên phải Console, chọn **US East (N. Virginia) us-east-1**. Mọi bước sau đều ở region này. Nếu tài nguyên vừa tạo "biến mất", **việc đầu tiên là kiểm tra region đang xem**.

---

## Giai đoạn 1: Dựng mạng

Vào **VPC Console** (gõ "VPC" ở ô tìm kiếm). Tên trong các bước dưới đây là **Name tag**, phải gõ đúng để bảng ở mục 8 và sơ đồ khớp nhau.

### 1.1 VPC

1. **Your VPCs → Create VPC**.
2. **Resources to create: VPC only**.
   > ⚠️ Không chọn "VPC and more": wizard đó tạo sẵn subnet, route table, có thể có cả **NAT Gateway** (tính tiền). Lab này cần tự tạo từng khối để hiểu.
3. Name tag: `roomsplit-lab-vpc`.
4. IPv4 CIDR block: **IPv4 CIDR manual input**, `10.20.0.0/16`.
5. IPv6: **No IPv6 CIDR block**. Tenancy: **Default** (Dedicated rất đắt).
6. Create VPC.
7. Chọn VPC vừa tạo → **Actions → Edit VPC settings** → bật **Enable DNS hostnames** (DNS resolution đã bật sẵn) → Save.

**Quan sát:** chỉ tạo một VPC nhưng AWS tự sinh thêm 3 thứ đi kèm:

| Tự sinh | Đặt Name tag | Vai trò |
|---|---|---|
| Main route table | `roomsplit-lab-rt-main` | Chỉ có route `10.20.0.0/16 → local`. **Không bao giờ thêm route vào bảng này** |
| Default Network ACL | `roomsplit-lab-nacl-default` | Cho phép tất cả, cả hai chiều |
| Default Security Group | `roomsplit-lab-sg-default` | Không dùng; không xóa được |

Đặt Name tag cho chúng: vào **Route tables / Network ACLs / Security groups**, lọc theo VPC, bấm vào ô Name (hình cây bút) để sửa. Nếu không đặt tên, các bảng chỉ hiện ID và rất dễ nhầm.

**Vì sao `/16`:** 65.536 địa chỉ, là mức lớn nhất AWS cho phép. VPC không đổi được CIDR chính sau khi tạo, nên lấy rộng ngay từ đầu. `10.20.x.x` không trùng default VPC (`172.31.x.x`) hay mạng Docker trên máy (`172.17–172.31.x.x`).

### 1.2 Subnet

1. **Subnets → Create subnet**, VPC ID: `roomsplit-lab-vpc`.
2. Điền subnet đầu tiên, bấm **Add new subnet** để thêm các subnet tiếp theo trong **cùng một form**:

| Subnet name | Availability Zone | IPv4 subnet CIDR block |
|---|---|---|
| `roomsplit-lab-public-a` | us-east-1a | `10.20.0.0/24` |
| `roomsplit-lab-public-b` | us-east-1b | `10.20.1.0/24` |
| `roomsplit-lab-app-a` | us-east-1a | `10.20.10.0/24` |
| `roomsplit-lab-app-b` | us-east-1b | `10.20.11.0/24` |
| `roomsplit-lab-data-a` | us-east-1a | `10.20.20.0/24` |
| `roomsplit-lab-data-b` | us-east-1b | `10.20.21.0/24` |

3. Create subnet.

**Quan sát:**
- Cột **Available IPv4 addresses** là **251**, không phải 256: AWS giữ 5 địa chỉ (`.0` network, `.1` router, `.2` DNS, `.3` để dành, `.255` broadcast).
- Cột **Route table**: cả 6 subnet đều đang dùng `rt-main`. Lúc này **cả 6 subnet đều private**, chưa có gì khác nhau.
- Chọn subnet → **Actions → Edit subnet settings**: ô **Auto-assign public IPv4** đang **tắt** (VPC tự tạo mặc định là tắt). Giữ nguyên như vậy.

### 1.3 Internet Gateway

1. **Internet gateways → Create internet gateway**, Name tag: `roomsplit-lab-igw` → Create.
2. Trạng thái là **Detached**. Bấm **Actions → Attach to VPC** → chọn `roomsplit-lab-vpc` → Attach.
3. Trạng thái chuyển sang **Attached**.

**Quan sát:** gắn IGW xong, subnet **vẫn chưa** ra được Internet. IGW chỉ là "cánh cửa"; phải có **route** chỉ đường tới cửa (bước 1.4).

### 1.4 Route table

**Tạo 4 bảng:** vào **Route tables → Create route table**, chọn VPC `roomsplit-lab-vpc`, lặp lại cho 4 tên:

`roomsplit-lab-rt-public`, `roomsplit-lab-rt-app-a`, `roomsplit-lab-rt-app-b`, `roomsplit-lab-rt-data`

Mỗi bảng mới tạo chỉ có route `local`.

**Thêm route ra Internet cho `rt-public`:**

1. Chọn `roomsplit-lab-rt-public` → tab **Routes → Edit routes → Add route**.
2. Destination `0.0.0.0/0`, Target **Internet Gateway** → `roomsplit-lab-igw`.
3. Save changes.

**Gắn subnet (tab Subnet associations → Edit subnet associations):**

| Route table | Chọn subnet |
|---|---|
| `roomsplit-lab-rt-public` | public-a, public-b |
| `roomsplit-lab-rt-app-a` | app-a |
| `roomsplit-lab-rt-app-b` | app-b |
| `roomsplit-lab-rt-data` | data-a, data-b |

**Quan sát (điểm quan trọng nhất của cả lab):** quay lại **Subnets**, xem cột Route table. `public-a/b` giờ dùng `rt-public` có `0.0.0.0/0 → igw` nên là **public**. Bốn subnet còn lại chỉ có `local`. Sáu subnet được tạo bằng cùng một form; thứ duy nhất khác nhau là **route table**.

Mở **Your VPCs → roomsplit-lab-vpc → tab Resource map** để xem sơ đồ subnet → route table → IGW do AWS tự vẽ, rồi so với [sơ đồ](aws-lab-architecture.html).

### 1.5 Gateway Endpoint S3

1. **Endpoints → Create endpoint**, Name tag: `roomsplit-lab-vpce-s3`.
2. Type: **AWS services**. Ô tìm kiếm Services gõ `s3`, chọn `com.amazonaws.us-east-1.s3` dòng có Type là **Gateway** (không chọn dòng Interface, vì Interface tính tiền theo giờ).
3. VPC: `roomsplit-lab-vpc`. Route tables: chọn **`rt-app-a`** và **`rt-app-b`**.
4. Policy: **Full access** → Create endpoint.

**Quan sát:** mở `rt-app-a` → Routes, thấy thêm dòng `pl-xxxx (com.amazonaws.us-east-1.s3) → vpce-…`. `pl-` là **prefix list**: danh sách dải IP của S3 do AWS quản lý. Gateway Endpoint thực chất là **một route**: app gọi S3 sẽ đi thẳng tới S3 trong mạng AWS, không cần NAT. Loại endpoint này miễn phí.

### 1.6 Security Group

SG tham chiếu lẫn nhau (`app` cho phép từ `alb`…), nên **tạo cả 6 SG rỗng trước, sau đó mới thêm rule**.

**Bước A: tạo SG rỗng.** Vào **Security groups → Create security group**, VPC = `roomsplit-lab-vpc`. Xóa các inbound rule mặc định (nếu có). Riêng outbound, làm như cột cuối của bảng:

> ⚠️ Ô **VPC** trên form tạo SG **mặc định là default VPC** (`172.31.0.0/16`), không nhớ lựa chọn lần trước. Mỗi lần tạo SG phải chọn lại `roomsplit-lab-vpc`. Nếu quên, SG nằm ở VPC khác và khi thêm rule, ô Source/Destination **chỉ liệt kê SG cùng VPC**, nên không thấy `roomsplit-lab-app` (rule tham chiếu SG không vượt VPC, trừ khi có VPC peering). SG cũng không đổi được VPC sau khi tạo: phải xóa và tạo lại. Kiểm tra nhanh: `aws ec2 describe-security-groups --filters "Name=group-name,Values=roomsplit-lab-*" --query "SecurityGroups[].[GroupName,VpcId]" --output table`, mọi dòng phải cùng một VpcId.

| Security group name | Description | Outbound lúc tạo |
|---|---|---|
| `roomsplit-lab-eice` | EC2 Instance Connect Endpoint | **Xóa** rule mặc định |
| `roomsplit-lab-test-public` | Lab EC2 in public subnet | Giữ All traffic |
| `roomsplit-lab-alb` | Load balancer (future) | **Xóa** |
| `roomsplit-lab-app` | API / app instances | Giữ All traffic |
| `roomsplit-lab-db` | MySQL / RDS | **Xóa** |
| `roomsplit-lab-redis` | Redis / ElastiCache | **Xóa** |

Tên SG (**Security group name**) **không đổi được** sau khi tạo, nên gõ cẩn thận. Đặt Name tag trùng với tên SG để cột Name trong bảng có giá trị.

**Bước B: thêm rule** (chọn SG → tab Inbound/Outbound rules → Edit). Ô Source/Destination: gõ `sg-` hoặc tên SG để chọn SG khác.

| SG | Chiều | Type | Port | Source / Destination |
|---|---|---|---|---|
| `eice` | Outbound | SSH | 22 | `roomsplit-lab-app` |
| `eice` | Outbound | SSH | 22 | `roomsplit-lab-test-public` |
| `test-public` | Inbound | SSH | 22 | `roomsplit-lab-eice` |
| `alb` | Inbound | HTTP | 80 | `0.0.0.0/0` |
| `alb` | Inbound | HTTPS | 443 | `0.0.0.0/0` |
| `alb` | Outbound | Custom TCP | 8080 | `roomsplit-lab-app` |
| `app` | Inbound | Custom TCP | 8080 | `roomsplit-lab-alb` |
| `app` | Inbound | SSH | 22 | `roomsplit-lab-eice` |
| `db` | Inbound | MYSQL/Aurora | 3306 | `roomsplit-lab-app` |
| `redis` | Inbound | Custom TCP | 6379 | `roomsplit-lab-app` |

**Quan sát:**
- `db` không có rule outbound nào nhưng vẫn trả lời được app. Đó là **stateful**: SG nhớ kết nối đi vào và tự cho gói trả lời đi ra.
- Không có rule nào dùng địa chỉ IP (trừ ALB nhận từ Internet). Một máy gắn SG `app` thì được vào DB, **bất kể IP của nó là gì**.

### 1.7 Network ACL cho tầng data

1. **Network ACLs → Create network ACL**, Name: `roomsplit-lab-nacl-data`, VPC: `roomsplit-lab-vpc` → Create.
2. Mở NACL vừa tạo: inbound và outbound đều chỉ có rule `*` **DENY**. Khác với default NACL (allow all), NACL tự tạo mặc định **chặn tất cả**.
3. **Inbound rules → Edit inbound rules → Add new rule:**

| Rule number | Type | Port range | Source | Allow/Deny |
|---|---|---|---|---|
| 100 | MySQL/Aurora (3306) | 3306 | `10.20.10.0/23` | Allow |
| 110 | Custom TCP | 6379 | `10.20.10.0/23` | Allow |

4. **Outbound rules → Edit outbound rules → Add new rule:**

| Rule number | Type | Port range | Destination | Allow/Deny |
|---|---|---|---|---|
| 100 | Custom TCP | 1024-65535 | `10.20.10.0/23` | Allow |

5. **Subnet associations → Edit subnet associations** → chọn `data-a`, `data-b` → Save.

**Vì sao cần outbound 1024–65535:** khi app (`10.20.10.5`, cổng nguồn ngẫu nhiên ví dụ `49152`) kết nối tới DB cổng 3306, gói trả lời đi **từ cổng 3306 tới cổng 49152** của app. NACL **stateless** không nhớ đã cho kết nối đi vào, nên phải mở chiều ra cho dải **cổng ephemeral** đó. Giai đoạn 4 sẽ thử xóa rule này để thấy kết nối bị timeout.

**Vì sao `10.20.10.0/23`:** `/23` có 512 địa chỉ, từ `10.20.10.0` đến `10.20.11.255`, phủ đúng app-a và app-b. Một rule thay được hai.

Kiểm tra: mở **Subnets**, cột **Network ACL** của data-a/b là `nacl-data`; bốn subnet còn lại là `nacl-default`.

### 1.8 Kiểm tra tổng thể

**Trên Console:** Your VPCs → `roomsplit-lab-vpc` → **Resource map**: 6 subnet, 5 route table (kể cả main), 1 IGW, 1 endpoint. Mũi tên tới IGW chỉ xuất phát từ `rt-public`.

**Bằng CLI** (profile `default` đã có region `us-east-1`):

```powershell
$Vpc = aws ec2 describe-vpcs --filters "Name=tag:Name,Values=roomsplit-lab-vpc" --query "Vpcs[0].VpcId" --output text

# Subnet: tên, AZ, CIDR, số IP còn trống
aws ec2 describe-subnets --filters "Name=vpc-id,Values=$Vpc" `
  --query "sort_by(Subnets,&CidrBlock)[].[Tags[?Key=='Name']|[0].Value,AvailabilityZone,CidrBlock,AvailableIpAddressCount,SubnetId]" --output table

# Route table: route và subnet được gắn
aws ec2 describe-route-tables --filters "Name=vpc-id,Values=$Vpc" `
  --query "RouteTables[].{Name:Tags[?Key=='Name']|[0].Value,Id:RouteTableId,Routes:Routes[].join('→',[DestinationCidrBlock||DestinationPrefixListId,GatewayId||NatGatewayId]),Subnets:Associations[].SubnetId}" --output json

# IGW, endpoint, SG, NACL
aws ec2 describe-internet-gateways --filters "Name=attachment.vpc-id,Values=$Vpc" --query "InternetGateways[].[InternetGatewayId,Attachments[0].State]" --output text
aws ec2 describe-vpc-endpoints --filters "Name=vpc-id,Values=$Vpc" --query "VpcEndpoints[].[VpcEndpointId,ServiceName,VpcEndpointType,RouteTableIds]" --output json
aws ec2 describe-security-groups --filters "Name=vpc-id,Values=$Vpc" --query "SecurityGroups[].[GroupName,GroupId]" --output table
aws ec2 describe-network-acls --filters "Name=vpc-id,Values=$Vpc" --query "NetworkAcls[].[Tags[?Key=='Name']|[0].Value,NetworkAclId,IsDefault,length(Associations)]" --output table
```

Khi xong, báo lại để Claude chạy kiểm tra và điền ID vào **mục 8** của [aws-lab-architecture.md](aws-lab-architecture.md).

**Chi phí giai đoạn 1: 0 USD.** Phần mạng này có thể để nguyên lâu dài.

---

## Giai đoạn 2: Deploy RoomSplit lên EC2

Phương án đã chốt (29/10/2026) trong [aws-ec2-deploy.md](aws-ec2-deploy.md): **E1 + M1 + I3 + A1 + P1**. Một EC2 `t3.small` ở **public-b** có IP public, chạy `mysql + redis + api` bằng chính `docker-compose.yml` + `docker-compose.prod.yml`. Image build ở máy dev rồi copy lên qua tunnel EICE. Cổng 80 chỉ mở cho **IP của bạn**.

```
Trình duyệt (IP của bạn) ─ HTTP :80 ─► IGW ─► EC2 roomsplit-lab-web (public-b) ─► api :8080
Terminal ─ aws ec2-instance-connect (API AWS) ─► EICE (app-a) ─ SSH :22 ─► EC2
```

Chi phí khi máy chạy: khoảng 0,026 USD/giờ (EC2 + IPv4), cộng EBS khoảng 1,6 USD/tháng. **Stop máy khi học xong.**

### 2.0 Hoàn thiện giai đoạn 1

Lần kiểm tra ngày 29/10 còn thiếu (xem [aws-ec2-deploy.md](aws-ec2-deploy.md) mục 1): rule của SG `app`, `db`, `alb`; SG `redis`; `nacl-data`; Name tag của main route table. Các mục này không chặn giai đoạn 2, nhưng nên làm xong để giai đoạn 4 không phải quay lại.

### 2.1 Cấp quyền EICE (đăng nhập root, làm một lần)

`dev-readonly` có `ec2:*` nhưng **thiếu** hai quyền thuộc namespace riêng `ec2-instance-connect:` (Claude đã kiểm tra bằng `iam simulate-principal-policy`), và không tạo được service-linked role mà EICE cần ở lần đầu:

| Quyền | Dùng để | `dev-readonly` |
|---|---|---|
| `ec2:CreateInstanceConnectEndpoint` | Tạo EICE | ✅ |
| `iam:CreateServiceLinkedRole` (role `AWSServiceRoleForEC2InstanceConnect`) | AWS tự tạo khi có EICE **đầu tiên** của tài khoản | ❌ |
| `ec2-instance-connect:OpenTunnel` | Mở tunnel SSH qua EICE | ❌ |
| `ec2-instance-connect:SendSSHPublicKey` | Đẩy key tạm 60 giây lên máy | ❌ |

**a) Tạo policy và gắn vào group `developers`:** vào **IAM → Policies → Create policy → JSON**, dán đoạn dưới (thay `<ACCOUNT_ID>` bằng số tài khoản, không ghi số này vào repo):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "OpenSshTunnelThroughEice",
      "Effect": "Allow",
      "Action": "ec2-instance-connect:OpenTunnel",
      "Resource": "arn:aws:ec2:us-east-1:<ACCOUNT_ID>:instance-connect-endpoint/*",
      "Condition": { "NumericEquals": { "ec2-instance-connect:remotePort": "22" } }
    },
    {
      "Sid": "PushTemporaryKeyAsEc2User",
      "Effect": "Allow",
      "Action": "ec2-instance-connect:SendSSHPublicKey",
      "Resource": "arn:aws:ec2:us-east-1:<ACCOUNT_ID>:instance/*",
      "Condition": { "StringEquals": { "ec2:osuser": "ec2-user" } }
    }
  ]
}
```

Tên policy: `roomsplit-lab-eice-connect`. Sau đó **IAM → User groups → developers → Permissions → Add permissions → Attach policies** → chọn policy vừa tạo.

Condition giới hạn: tunnel chỉ tới cổng 22, key tạm chỉ cho user `ec2-user`. Cách đọc Condition: [iam-notes.md](iam-notes.md) mục 4.5.

**b) Tạo EICE (vẫn bằng root, để AWS tạo service-linked role):**

1. **VPC → Endpoints → Create endpoint**, Name tag: `roomsplit-lab-eice`.
2. Type: **EC2 Instance Connect Endpoint**. VPC: `roomsplit-lab-vpc`.
3. Security groups: `roomsplit-lab-eice`. Subnet: `roomsplit-lab-app-a`.
4. **Preserve Client IP: tắt.** Nếu bật, máy đích sẽ thấy IP nhà bạn thay vì IP của EICE, và SG `web` phải mở 22 cho IP nhà.
5. Create. Trạng thái từ `create-in-progress` sang `create-complete` mất vài phút.

Mỗi VPC chỉ có một EICE. EICE miễn phí; giữ lại được lâu dài.

### 2.2 Security Group `roomsplit-lab-web`

Đăng nhập lại bằng `dev-readonly`. **Security groups → Create security group**, name `roomsplit-lab-web`, description `RoomSplit lab single host`, VPC `roomsplit-lab-vpc`:

| Chiều | Type | Port | Source / Destination | Ghi chú |
|---|---|---|---|---|
| Inbound | HTTP | 80 | **My IP** | Console tự điền `x.x.x.x/32`. Đây là phương án A1 |
| Inbound | SSH | 22 | `roomsplit-lab-eice` | |
| Outbound | All traffic | All | `0.0.0.0/0` | Giữ mặc định: máy cần tải Docker, pull `mysql`/`redis` |

Sửa SG `roomsplit-lab-eice` → Outbound → thêm **SSH 22 → `roomsplit-lab-web`**.

Không có rule SSH từ Internet: cổng 22 chỉ nhận từ EICE.

### 2.3 Key pair (máy dev)

Tạo key ở máy, chỉ đưa **public key** lên AWS (private key không rời máy):

```powershell
New-Item -ItemType Directory -Force $HOME\.ssh | Out-Null
ssh-keygen -t ed25519 -f $HOME\.ssh\roomsplit-lab -C roomsplit-lab
```

Console: **EC2 → Key pairs → Actions → Import key pair**, name `roomsplit-lab-key`, chọn file `roomsplit-lab.pub` → Import.

### 2.4 Launch instance

**EC2 → Instances → Launch instances:**

| Mục | Giá trị |
|---|---|
| Name | `roomsplit-lab-web` |
| AMI | **Amazon Linux 2023** (kernel 6.x), **64-bit (x86)** |
| Instance type | **t3.small** (kiểm tra nhãn Free tier eligible) |
| Key pair | `roomsplit-lab-key` |
| Network settings → **Edit** | VPC `roomsplit-lab-vpc`; Subnet `roomsplit-lab-public-b`; **Auto-assign public IP: Enable**; Select existing security group: `roomsplit-lab-web` |
| Storage | 20 GiB **gp3** |
| Advanced details → Credit specification | **Standard** (mặc định của `t3` là Unlimited: chạy CPU cao lâu sẽ tính thêm tiền) |
| Advanced details → User data | Đoạn dưới |

```bash
#!/bin/bash
# Runs once, as root, on first boot. Log: /var/log/cloud-init-output.log
set -euxo pipefail

dnf install -y docker
systemctl enable --now docker
usermod -aG docker ec2-user

# Docker Compose v2 plugin (not packaged for Amazon Linux 2023)
mkdir -p /usr/local/lib/docker/cli-plugins
curl -fsSL https://github.com/docker/compose/releases/download/v2.39.1/docker-compose-linux-x86_64 \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

# 1 GB swap: safety net for 2 GB RAM (mysql 1g + api 512m + redis 128m limits)
dd if=/dev/zero of=/swapfile bs=1M count=1024
chmod 600 /swapfile
mkswap /swapfile
swapon /swapfile
echo '/swapfile swap swap defaults 0 0' >> /etc/fstab

mkdir -p /opt/roomsplit
chown ec2-user:ec2-user /opt/roomsplit
```

Launch instance. Khi **Instance state = Running** và **Status check = 3/3 checks passed**, ghi lại **Instance ID** và **Public IPv4 address**.

**Quan sát (bài thí nghiệm public/private):** máy có IP public **và** nằm trong subnet có route `0.0.0.0/0 → igw`. Thiếu một trong hai thì máy không ra/vào Internet được. User data chạy được `dnf install`, `curl github.com` chính là bằng chứng máy đã ra được Internet.

### 2.5 SSH vào máy qua EICE

Mở PowerShell ở máy dev, thay `i-…` bằng Instance ID:

```powershell
$Id = "i-…"
aws ec2-instance-connect ssh --instance-id $Id --connection-type eice `
  --os-user ec2-user --private-key-file $HOME\.ssh\roomsplit-lab
```

Trong máy:

```bash
docker --version && docker compose version     # cả hai phải chạy
free -h                                         # thấy Swap 1.0Gi
sudo tail -n 5 /var/log/cloud-init-output.log   # user data chạy xong, không lỗi
curl -s https://checkip.amazonaws.com           # IP trả về = Public IPv4 của máy
exit
```

**Quan sát:** lệnh SSH đi tới **API của AWS**, rồi EICE mở kết nối nội bộ tới `10.20.1.x:22`. Không cần mở cổng 22 ra Internet.

### 2.6 Build image và chuẩn bị file (máy dev)

Chạy ở thư mục gốc repo:

```powershell
$Tag = git describe --tags --always --dirty
New-Item -ItemType Directory -Force artifacts | Out-Null

# 1. Image (linux/amd64, khớp t3)
docker build -t "roomsplit-api:$Tag" .
docker save -o "artifacts/roomsplit-api-$Tag.tar" "roomsplit-api:$Tag"
tar -czf "artifacts/roomsplit-api-$Tag.tar.gz" -C artifacts "roomsplit-api-$Tag.tar"

# 2. Migration script idempotent
Push-Location RoomSplit.Api
dotnet ef migrations script --idempotent -o ../artifacts/migrate.sql
Pop-Location

# 3. Env file: copy template, điền mật khẩu mới (khác staging/prod) và API_TAG = $Tag
Copy-Item envs/aws-lab.env.example envs/aws-lab.env
$Tag
```

Mở `envs/aws-lab.env`, điền `API_TAG` (giá trị `$Tag` vừa in), `MYSQL_ROOT_PASSWORD`, `MYSQL_PASSWORD`, `REDIS_PASSWORD`. File này đã bị git-ignore (`*.env`).

### 2.7 Copy file lên EC2

`scp` đi qua EICE bằng `ProxyCommand`: lệnh `open-tunnel` không có `--local-port` sẽ nối thẳng stdin/stdout vào tunnel.

```powershell
$Proxy = "ProxyCommand=aws ec2-instance-connect open-tunnel --instance-id $Id"
scp -i $HOME\.ssh\roomsplit-lab -o $Proxy -o HostKeyAlias=roomsplit-lab-web `
  "artifacts/roomsplit-api-$Tag.tar.gz" artifacts/migrate.sql `
  docker-compose.yml docker-compose.prod.yml envs/aws-lab.env `
  ec2-user@roomsplit-lab-web:/opt/roomsplit/
```

Lần đầu `scp` hỏi xác nhận host key, gõ `yes`. `HostKeyAlias` giúp lưu host key dưới tên máy, không phải dưới `$Id`.

### 2.8 Chạy RoomSplit trên EC2

SSH vào máy (lệnh ở 2.5), rồi:

```bash
cd /opt/roomsplit
TAG=$(grep '^API_TAG=' aws-lab.env | cut -d= -f2)
docker load -i "roomsplit-api-$TAG.tar.gz"

alias dc='docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file aws-lab.env'
dc up -d
dc ps                      # mysql, redis: healthy; api: running

# Tạo schema (script idempotent, chạy lại không hỏng)
dc exec -T mysql sh -c 'mysql -u"$MYSQL_USER" -p"$MYSQL_PASSWORD" roomsplit' < migrate.sql

curl -s localhost/health/ready      # Healthy
curl -s localhost/api/v1/categories # 6 category seed
```

`docker load` nhận thẳng file `.tar.gz`. Container `mysql` không mở cổng ra máy chủ; `api` nghe 8080 trong container, compose map ra **cổng 80** của máy (`API_PORT=80`).

### 2.9 Truy cập từ Internet

Trên trình duyệt máy dev:

| URL | Kết quả mong đợi |
|---|---|
| `http://<public-ip>/health/ready` | `Healthy` |
| `http://<public-ip>/swagger` | Swagger UI (môi trường Staging bật Swagger) |
| `POST /api/v1/rooms` trong Swagger | Tạo được room, `GET` thấy lại |

**Thí nghiệm:**

| Làm | Thấy | Vì sao |
|---|---|---|
| Mở `http://<public-ip>/swagger` bằng điện thoại dùng **4G** (không qua wifi nhà) | **Timeout** | SG chỉ cho IP nhà; gói tin bị bỏ im lặng, không phải "refused" |
| Gỡ route `0.0.0.0/0 → igw` khỏi `rt-public` (nhớ trả lại ngay) | Trình duyệt máy dev cũng timeout | Có IP public nhưng không có đường tới IGW thì vẫn không vào được |
| **Stop** rồi **Start** máy | Public IPv4 **đổi**; dữ liệu room vẫn còn; container tự chạy lại (`restart: unless-stopped`) | IP tự gán bị trả lại khi stop; volume `mysql-data` nằm trên EBS |

### 2.10 Kết thúc buổi học

- **EC2 → Instances → Instance state → Stop instance.** Không chọn Terminate: terminate là mất ổ đĩa và dữ liệu.
- Khi stop, chỉ còn EBS tính tiền (khoảng 1,6 USD/tháng).
- Cập nhật phiên bản mới: làm lại 2.6–2.8 với tag mới (`dc up -d` sẽ thay container `api`, MySQL giữ nguyên).
- Nếu IP nhà đổi: sửa inbound 80 của SG `web` → chọn lại **My IP**.

Báo Claude Instance ID khi xong để kiểm tra và ghi vào mục 8 của [aws-lab-architecture.md](aws-lab-architecture.md).

---

## Lỗi hay gặp

| Triệu chứng | Nguyên nhân | Cách xử lý |
|---|---|---|
| Không thấy VPC/subnet vừa tạo | Đang xem region khác | Chọn lại N. Virginia |
| Tạo subnet báo lỗi CIDR | Gõ nhầm (ví dụ `10.20.10.0/24` trùng với subnet đã tạo, hoặc nằm ngoài `10.20.0.0/16`) | Đối chiếu bảng ở 1.2 |
| Không thêm được route `0.0.0.0/0 → igw` | IGW chưa attach vào VPC | Làm lại bước 1.3 |
| Không chọn được SG khác làm Source | SG đó thuộc VPC khác (ví dụ default VPC) | Tạo SG trong `roomsplit-lab-vpc` |
| Lỡ tạo bằng "VPC and more" | Có thể đã có NAT Gateway | Vào **NAT gateways** xóa ngay, rồi **Elastic IPs** → Release; sau đó xóa VPC đó và làm lại |
| `AccessDenied` khi tạo Budget bằng `dev-readonly` | User không có quyền Budgets | Làm bằng root (bước 0.2) |
| `AccessDeniedException ... ec2-instance-connect:OpenTunnel` | Chưa gắn policy `roomsplit-lab-eice-connect` | Bước 2.1a |
| EICE kẹt ở `create-failed` | Tài khoản chưa có service-linked role, tạo bằng `dev-readonly` | Xóa, tạo lại bằng root (2.1b) |
| SSH qua EICE treo rồi timeout | SG `eice` thiếu outbound 22 → `web`, hoặc SG `web` thiếu inbound 22 từ `eice` | Bước 2.2 |
| `Permission denied (publickey)` | Sai key hoặc sai user (`ec2-user`, không phải `ubuntu`/`root`) | Kiểm tra `--private-key-file`, `--os-user` |
| Không có `docker` / `docker compose` | User data chưa chạy xong hoặc lỗi | `sudo tail -n 50 /var/log/cloud-init-output.log` |
| `permission denied ... docker.sock` | Phiên SSH mở trước khi `ec2-user` vào group `docker` | Thoát rồi SSH lại |
| Container `mysql` restart liên tục, `dmesg` có `Out of memory` | Thiếu RAM (nhầm `t3.micro` hoặc không có swap) | Dùng `t3.small`, kiểm tra `free -h` |
| `/health/ready` trả `Unhealthy` | Sai mật khẩu trong `aws-lab.env`, hoặc MySQL chưa healthy | `dc logs api`, `dc ps` |
| API trả 500 khi gọi `/api/v1/rooms` | Chưa chạy `migrate.sql` | Bước 2.8 |
| Trình duyệt timeout dù máy đang chạy | IP nhà đã đổi so với rule My IP, hoặc gõ `https://` | Sửa SG; dùng `http://` |
