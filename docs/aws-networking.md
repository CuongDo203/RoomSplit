# AWS Networking: từ VPC đến gói tin

Tài liệu học tập, tiếp theo [iam-notes.md](iam-notes.md). IAM trả lời câu hỏi **"ai được gọi API gì"**, còn networking trả lời **"gói tin có đi được từ A tới B không"**. Hai lớp này độc lập với nhau: có quyền IAM đầy đủ mà Security Group chặn cổng 3306 thì app vẫn không kết nối được RDS.

Kiến thức nền nên đọc trước: [docker-volumes-networks.md](docker-volumes-networks.md) mục 2. Rất nhiều khái niệm của AWS có "bản thu nhỏ" trong Docker (xem [mục 15](#15-so-sánh-với-docker-networking-đã-học)).

> ⚠️ Các lệnh `aws ec2 ...` ở [mục 17](#17-lab-dựng-một-vpc-bằng-cli) **chưa được chạy**. Giá dịch vụ trong bài là **mức tham khảo** (region `us-east-1`, có thể đã đổi; region Singapore thường đắt hơn một chút). Trước khi tạo tài nguyên tốn tiền, hãy xem trang AWS Pricing.

## Mục lục
1. [Sửa lại sơ đồ ban đầu](#1-sửa-lại-sơ-đồ-ban-đầu)
2. [Hạ tầng toàn cầu: Region, AZ, Edge](#2-hạ-tầng-toàn-cầu-region-az-edge)
3. [VPC và CIDR](#3-vpc-và-cidr)
4. [Subnet](#4-subnet)
5. [Route table](#5-route-table)
6. [Internet Gateway, public IP, Elastic IP](#6-internet-gateway-public-ip-elastic-ip)
7. [NAT Gateway: cho private subnet đi ra Internet](#7-nat-gateway-cho-private-subnet-đi-ra-internet)
8. [Security Group và Network ACL](#8-security-group-và-network-acl)
9. [Load Balancer: ALB, NLB](#9-load-balancer-alb-nlb)
10. [RDS và ElastiCache trong VPC](#10-rds-và-elasticache-trong-vpc)
11. [DNS: Route 53 và DNS trong VPC](#11-dns-route-53-và-dns-trong-vpc)
12. [VPC Endpoint: gọi dịch vụ AWS không qua Internet](#12-vpc-endpoint-gọi-dịch-vụ-aws-không-qua-internet)
13. [Kết nối VPC với VPC, với văn phòng, với admin](#13-kết-nối-vpc-với-vpc-với-văn-phòng-với-admin)
14. [Theo dấu một request từ đầu đến cuối](#14-theo-dấu-một-request-từ-đầu-đến-cuối)
15. [So sánh với Docker networking đã học](#15-so-sánh-với-docker-networking-đã-học)
16. [Kiến trúc mạng đề xuất cho RoomSplit](#16-kiến-trúc-mạng-đề-xuất-cho-roomsplit)
17. [Lab: dựng một VPC bằng CLI](#17-lab-dựng-một-vpc-bằng-cli)
18. [Quan sát và gỡ lỗi](#18-quan-sát-và-gỡ-lỗi)
19. [Chi phí: những thứ hay "đốt tiền" bất ngờ](#19-chi-phí-những-thứ-hay-đốt-tiền-bất-ngờ)
20. [Lỗi hay gặp và bảng tra cứu nhanh](#20-lỗi-hay-gặp-và-bảng-tra-cứu-nhanh)

---

## 1. Sửa lại sơ đồ ban đầu

Sơ đồ bạn vẽ đã có đủ các khối chính (VPC, Subnet, Routing, Security). Có ba chỗ cần sửa vì sẽ dẫn đến hiểu sai:

| Trong sơ đồ | Thực tế | Vì sao quan trọng |
|---|---|---|
| NAT nằm dưới EC2 trong **Private** | NAT Gateway nằm trong **Public subnet**. Private subnet chỉ có một **route** trỏ tới NAT | NAT phải đi được ra Internet Gateway thì mới làm "người đi chợ hộ" cho private subnet được |
| RDS nối với cả Public và Private | RDS nằm trong **private subnet riêng** (data subnet), chỉ nhận kết nối từ app | Database không bao giờ nên có đường từ Internet vào |
| Routing tách riêng, Public/Private là hai loại subnet | Subnet **public hay private là do route table quyết định**. Không có ô "chọn loại subnet" nào khi tạo subnet | Đây là khái niệm quan trọng nhất của bài (mục 4, 5) |

Ngoài ra còn thiếu hai thứ: **Internet Gateway** (cửa ra vào Internet của VPC) và **Availability Zone** (mỗi subnet nằm gọn trong một AZ, production luôn dùng tối thiểu 2 AZ).

Sơ đồ đã sửa (một AZ; thực tế nhân đôi sang AZ thứ hai):

```
                              Internet
                                 │
                       ┌─────────▼─────────┐
                       │  Internet Gateway │  (gắn vào VPC)
                       └─────────┬─────────┘
┌─ VPC 10.20.0.0/16 ─────────────┼────────────────────────────────────────┐
│                                │                                        │
│  ┌─ Public subnet 10.20.0.0/24 ▼ ─────────────────────────────────┐     │
│  │   route: 0.0.0.0/0 → IGW                                       │     │
│  │                                                                │     │
│  │   [ALB]  ◄── người dùng vào              [NAT Gateway]         │     │
│  └─────┬──────────────────────────────────────────▲───────────────┘     │
│        │ :8080                                    │ đi ra (pull image,  │
│        ▼                                          │ gọi API ngoài)      │
│  ┌─ Private app subnet 10.20.10.0/24 ─────────────┴───────────────┐     │
│  │   route: 0.0.0.0/0 → NAT                                       │     │
│  │                                                                │     │
│  │   [EC2 / ECS task: RoomSplit API]                              │     │
│  └─────┬──────────────────────────────────────────────────────────┘     │
│        │ :3306, :6379                                                   │
│        ▼                                                                │
│  ┌─ Private data subnet 10.20.20.0/24 ────────────────────────────┐     │
│  │   route: chỉ có local (không ra Internet)                      │     │
│  │                                                                │     │
│  │   [RDS MySQL]   [ElastiCache Redis]                            │     │
│  └────────────────────────────────────────────────────────────────┘     │
│                                                                         │
│  Security Group: gắn vào ALB / EC2 / RDS (tường lửa từng máy, stateful) │
│  Network ACL:    gắn vào từng subnet (tường lửa cửa subnet, stateless)  │
└─────────────────────────────────────────────────────────────────────────┘
```

Mũi tên chỉ chiều **khởi tạo kết nối**. Chiều trả lời tự đi ngược lại (mục 8 giải thích vì sao).

---

## 2. Hạ tầng toàn cầu: Region, AZ, Edge

| Khái niệm | Là gì | Ví dụ |
|---|---|---|
| **Region** | Một vùng địa lý độc lập, gồm nhiều data center | `ap-southeast-1` (Singapore), `ap-southeast-7` (Thái Lan), `us-east-1` (Virginia) |
| **Availability Zone (AZ)** | Một hoặc nhiều data center trong region, có điện, mạng, làm mát riêng; các AZ cách nhau đủ xa để không chết cùng lúc nhưng đủ gần để độ trễ chỉ khoảng 1–2 ms | `ap-southeast-1a`, `ap-southeast-1b`, `ap-southeast-1c` |
| **Edge location** | Điểm hiện diện gần người dùng, dùng cho CloudFront (CDN), Route 53, Global Accelerator | Có ở Hà Nội, TP.HCM |

Quy tắc:

- **VPC thuộc một region** và trải qua mọi AZ của region đó.
- **Subnet thuộc đúng một AZ.** Muốn chịu được sự cố một AZ, phải có subnet ở ít nhất 2 AZ và chạy app ở cả hai.
- Tên `ap-southeast-1a` của tài khoản bạn có thể là một AZ vật lý khác `ap-southeast-1a` của tài khoản khác. ID cố định thật là **AZ ID** (ví dụ `apse1-az1`).
- Chọn region gần người dùng: với người dùng Việt Nam thường là Singapore (khoảng 30–50 ms).

---

## 3. VPC và CIDR

**VPC** (Virtual Private Cloud) là một **mạng riêng ảo, cô lập** của bạn trong một region. Mặc định không có gì đi vào hay đi ra được VPC; bạn phải chủ động mở cửa (Internet Gateway, NAT, peering…).

### 3.1 CIDR là gì

Khi tạo VPC, bạn chọn **dải IP** cho nó, viết theo dạng **CIDR**: `địa chỉ/số bit cố định`.

```
10.20.0.0/16
│         └─ 16 bit đầu cố định (10.20), 16 bit sau tự do
└─ địa chỉ mạng

Số địa chỉ = 2^(32 − 16) = 65.536   (10.20.0.0 → 10.20.255.255)
```

| CIDR | Số IP | Thường dùng cho |
|---|---|---|
| `/16` | 65.536 | VPC (lớn nhất AWS cho phép) |
| `/20` | 4.096 | Subnet cho hệ thống lớn, EKS |
| `/24` | 256 (dùng được 251) | Subnet thông thường, dễ đọc |
| `/28` | 16 (dùng được 11) | Subnet nhỏ nhất AWS cho phép |
| `/32` | 1 | Một IP duy nhất (trong rule SG, route) |
| `0.0.0.0/0` | Tất cả | "Mọi nơi", ví dụ route mặc định ra Internet |

### 3.2 Chọn dải nào

Dùng dải **private** (RFC 1918), không đi được trên Internet:

```
10.0.0.0/8        (10.0.0.0 – 10.255.255.255)
172.16.0.0/12     (172.16.0.0 – 172.31.255.255)
192.168.0.0/16    (192.168.0.0 – 192.168.255.255)
```

Kinh nghiệm thực tế: **lên kế hoạch CIDR ngay từ đầu**, vì không đổi được CIDR chính của VPC sau khi tạo (chỉ thêm được CIDR phụ). Tránh trùng với:

- **VPC khác** bạn có thể sẽ nối vào (peering, Transit Gateway không cho phép trùng dải).
- **Mạng văn phòng/nhà** nếu sau này nối VPN (rất nhiều router gia đình dùng `192.168.0.0/24`, `192.168.1.0/24`).
- **Docker**: bridge mặc định dùng `172.17.0.0/16`, các network compose dùng tiếp `172.18.x`, `172.19.x`…

Vì vậy bài này dùng `10.20.0.0/16`: ít khả năng đụng hàng.

### 3.3 Default VPC

Mỗi region có sẵn một **default VPC** (`172.31.0.0/16`), mọi subnet đều public, máy mới tạo tự có public IP. Tiện để thử nhanh, **không dùng cho thứ gì nghiêm túc**: tạo VPC riêng.

### 3.4 Thuộc tính DNS của VPC

Hai cờ nên bật (VPC tự tạo qua CLI có thể chưa bật `enableDnsHostnames`):

- `enableDnsSupport`: VPC có DNS resolver (mục 11).
- `enableDnsHostnames`: máy có public IP được cấp tên DNS public. **Bắt buộc** cho Interface Endpoint với private DNS (mục 12).

---

## 4. Subnet

**Subnet** chia nhỏ dải IP của VPC, mỗi subnet nằm trong **một AZ**.

```
VPC 10.20.0.0/16
├── 10.20.0.0/24   public-a   (ap-southeast-1a)
├── 10.20.1.0/24   public-b   (ap-southeast-1b)
├── 10.20.10.0/24  app-a      (ap-southeast-1a)
├── 10.20.11.0/24  app-b      (ap-southeast-1b)
├── 10.20.20.0/24  data-a     (ap-southeast-1a)
└── 10.20.21.0/24  data-b     (ap-southeast-1b)
```

### 4.1 Năm địa chỉ bị AWS giữ lại

Trong mỗi subnet, AWS giữ 5 IP. Với `10.20.10.0/24`:

| IP | Dùng cho |
|---|---|
| `10.20.10.0` | Địa chỉ mạng |
| `10.20.10.1` | VPC router (gateway của subnet) |
| `10.20.10.2` | DNS resolver (thực ra là IP của VPC + 2, ví dụ `10.20.0.2`; AWS giữ `.2` ở mọi subnet) |
| `10.20.10.3` | Dự phòng cho tương lai |
| `10.20.10.255` | Broadcast (VPC không hỗ trợ broadcast nhưng vẫn giữ) |

Nên một `/24` chỉ còn **251** IP dùng được.

### 4.2 Public, private, isolated: do route table quyết định

| Loại | Route table có… | Máy trong đó |
|---|---|---|
| **Public** | `0.0.0.0/0 → Internet Gateway` | Có public IP thì nhận kết nối từ Internet được (nếu SG cho phép) |
| **Private** | `0.0.0.0/0 → NAT Gateway` | Đi ra Internet được, **không ai từ Internet vào được** |
| **Isolated** | Chỉ có route `local` | Chỉ nói chuyện trong VPC (hợp với database) |

Thêm một cờ của subnet: `MapPublicIpOnLaunch`, tức là tự cấp public IPv4 cho máy mới. Bật ở public subnet nếu cần, **tắt** ở private subnet.

> 💡 Câu thần chú: **"Subnet là public khi route table của nó có đường tới Internet Gateway."** Đặt tên `public-a` mà quên route thì nó vẫn là private.

### 4.3 Nên chia bao nhiêu tầng

Mẫu **3 tầng × 2 AZ** như trên là chuẩn phổ biến:

- **Public**: chỉ những thứ phải đối mặt Internet (ALB, NAT Gateway, bastion nếu có). **Không đặt app server ở đây.**
- **App (private)**: EC2, ECS task, Lambda trong VPC.
- **Data (isolated)**: RDS, ElastiCache.

---

## 5. Route table

**Route table** là bảng chỉ đường: "gói tin đi tới đích X thì giao cho cửa Y". Mỗi subnet được gắn (associate) với **đúng một** route table; một route table dùng được cho nhiều subnet.

Ví dụ route table của public subnet:

| Destination | Target | Ý nghĩa |
|---|---|---|
| `10.20.0.0/16` | `local` | Mọi IP trong VPC: đi thẳng. Có sẵn, **không xóa được** |
| `0.0.0.0/0` | `igw-0abc…` | Mọi thứ còn lại: ra Internet Gateway |

Route table của private app subnet ở AZ a:

| Destination | Target |
|---|---|
| `10.20.0.0/16` | `local` |
| `0.0.0.0/0` | `nat-0aaa…` (NAT Gateway **cùng AZ a**) |
| `pl-6fa54006` (prefix list của S3) | `vpce-0s3…` (Gateway Endpoint, mục 12) |

Quy tắc chọn route: **longest prefix match**, tức là route cụ thể nhất thắng. Gói tin đi `10.20.20.15` khớp cả `10.20.0.0/16` và `0.0.0.0/0` → chọn `/16` (cụ thể hơn) → đi local.

**Main route table**: route table mặc định của VPC; subnet nào không được gắn route table riêng thì dùng nó. Kinh nghiệm: **để main route table là loại an toàn nhất** (chỉ local) và gắn rõ ràng route table cho từng subnet. Như vậy nếu ai đó tạo subnet mới mà quên gắn, subnet đó mặc định là isolated chứ không vô tình public.

Các target khác sẽ gặp: VPC peering (`pcx-`), Transit Gateway (`tgw-`), Virtual Private Gateway (`vgw-`), Egress-only IGW (`eigw-`, cho IPv6), ENI của một firewall appliance.

---

## 6. Internet Gateway, public IP, Elastic IP

### 6.1 Internet Gateway (IGW)

- Một VPC gắn **tối đa một** IGW. IGW được AWS quản lý: tự dự phòng, tự scale, **miễn phí** (bạn chỉ trả tiền data transfer).
- IGW làm **NAT 1–1** giữa public IP và private IP: máy EC2 thực ra chỉ biết IP private `10.20.0.25` của nó, chạy `ip addr` trong máy sẽ không thấy public IP. IGW đổi `54.x.x.x ↔ 10.20.0.25` khi gói tin đi qua.

Để một máy **nhận kết nối từ Internet** cần đủ cả 4:

1. Subnet có route `0.0.0.0/0 → IGW`.
2. Máy có **public IPv4** hoặc **Elastic IP** (hoặc IPv6).
3. **Security Group** cho phép cổng đó từ nguồn đó.
4. **NACL** cho phép cả chiều vào lẫn chiều ra (mặc định là cho phép hết).

Thiếu một trong bốn là timeout. Đây là checklist đầu tiên khi "không vào được máy".

### 6.2 Public IP và Elastic IP

| | Public IPv4 tự cấp | Elastic IP (EIP) |
|---|---|---|
| Cấp khi | Khởi tạo máy trong subnet bật `MapPublicIpOnLaunch` | Bạn chủ động xin, rồi gắn vào máy/NAT |
| Khi stop/start máy | **Đổi IP mới** | Giữ nguyên |
| Chi phí | Có tính phí | Có tính phí, kể cả khi **không gắn vào đâu** |

Từ 2024 AWS tính phí **mọi public IPv4** (khoảng 0,005 USD/giờ, tức khoảng 3,6 USD/tháng mỗi IP). Thêm một lý do để chỉ ALB và NAT có public IP, còn app server thì không.

### 6.3 IPv6 nói nhanh

VPC có thể thêm dải IPv6 `/56`. IPv6 trên AWS **đều là địa chỉ public** (không có NAT); để máy chỉ đi ra mà không cho vào thì dùng **Egress-only Internet Gateway**. Người mới có thể bỏ qua IPv6 lúc đầu.

---

## 7. NAT Gateway: cho private subnet đi ra Internet

Máy trong private app subnet vẫn cần đi ra ngoài: pull Docker image, `apt update`, gọi API bên thứ ba, gửi log. **NAT Gateway** làm việc đó:

```
EC2 10.20.10.5 ──► NAT Gateway (trong public subnet, có Elastic IP 13.x.x.x)
                     đổi nguồn 10.20.10.5 → 13.x.x.x
                     ──► IGW ──► Internet
Phản hồi quay về 13.x.x.x ──► NAT nhớ bảng phiên ──► trả về 10.20.10.5
```

- NAT chỉ cho **đi ra rồi nhận phản hồi**. Không ai ngoài Internet chủ động mở kết nối vào được máy private qua NAT.
- NAT Gateway nằm trong **một AZ**. Kiến trúc chuẩn: **mỗi AZ một NAT**, route table của private subnet AZ nào trỏ về NAT AZ đó. Dùng chung một NAT thì rẻ hơn nhưng AZ chứa NAT chết thì cả hai AZ mất Internet, cộng thêm phí truyền dữ liệu chéo AZ.
- **Đắt** (với dự án nhỏ): khoảng 0,045 USD/giờ (khoảng 32 USD/tháng mỗi NAT) **cộng** 0,045 USD cho mỗi GB đi qua. Pull image vài GB mỗi lần deploy là thấy ngay trên hóa đơn.

Cách giảm chi phí NAT:

| Cách | Ý tưởng |
|---|---|
| **VPC Endpoint** (mục 12) | Lưu lượng tới S3, ECR, CloudWatch… đi đường nội bộ, không qua NAT |
| Một NAT cho dev/staging | Chấp nhận rủi ro mất một AZ |
| NAT instance | EC2 nhỏ tự làm NAT. Rẻ, nhưng tự lo vá lỗi, dự phòng |
| Không dùng NAT ở môi trường học | App đặt trong public subnet với SG chặt (xem mục 16.5) |

---

## 8. Security Group và Network ACL

Hai lớp tường lửa, khác nhau ở **vị trí** và **tính trạng thái (stateful)**.

### 8.0 Security Group là gì (cho người mới)

**SG = Security Group**: một **tường lửa ảo gắn vào từng tài nguyên** (EC2, RDS, ALB, ECS task…), quyết định **gói tin nào được vào, được ra** tài nguyên đó.

Ví von phòng trọ: SG là **bác bảo vệ đứng ngay cửa từng phòng**, cầm một danh sách "ai được vào, qua cửa nào". Không có tên trong danh sách thì không được vào. Bác nhớ mặt người đã cho vào, nên khi người đó đi ra thì không hỏi lại (đây là "stateful").

Một SG là **danh sách rule**, mỗi rule gồm 4 phần:

| Phần | Ý nghĩa | Ví dụ |
|---|---|---|
| Chiều | Inbound (vào) hoặc Outbound (ra) | Inbound |
| Protocol | TCP, UDP, ICMP, hoặc All | TCP |
| Port | Cổng hoặc dải cổng | `3306` |
| Nguồn / đích | CIDR (dải IP), **SG khác**, hoặc prefix list | `sg-app` |

Đọc rule trên: "**Cho phép vào** TCP cổng **3306** từ **mọi thứ gắn SG `sg-app`**". Gắn SG này vào RDS nghĩa là chỉ app mới kết nối được MySQL.

Những điều cần biết:

- SG **chỉ có Allow**. Không có rule nào khớp thì bị chặn, **bỏ im lặng** (phía gọi thấy **timeout**, không thấy báo lỗi).
- SG mới tạo: **inbound chặn hết, outbound cho hết**.
- Thực chất SG gắn vào **ENI** (card mạng ảo). Một ENI gắn được nhiều SG (mặc định tối đa 5); quyền cuối cùng là **hợp** của tất cả rule.
- Một SG dùng chung cho nhiều tài nguyên được, và **thuộc về một VPC**.
- Sửa rule có hiệu lực **ngay lập tức**, không cần khởi động lại máy.
- Mỗi VPC có sẵn một SG tên `default`: các thành viên của nó gọi nhau thoải mái. Đừng dùng nó; hãy tạo SG riêng có tên rõ nghĩa.

So với Docker: trong compose, container cùng network gọi nhau tự do và chỉ cổng nào `ports:` publish thì mới vào được từ host. AWS chặt hơn: kể cả hai máy cùng VPC cũng **không** gọi nhau được nếu SG không mở.

```
             ┌──────────── Subnet ────────────┐
Gói tin ──►  │ NACL (cửa subnet, stateless)   │
             │      ┌──── ENI của EC2 ────┐   │
             │      │ Security Group      │   │
             │      │ (stateful)   [EC2]  │   │
             │      └─────────────────────┘   │
             └────────────────────────────────┘
```

### 8.1 So sánh

| | Security Group (SG) | Network ACL (NACL) |
|---|---|---|
| Gắn vào | **ENI** (card mạng của EC2, RDS, ALB, Lambda, ECS task…) | **Subnet** |
| Trạng thái | **Stateful**: cho vào thì phản hồi tự được ra | **Stateless**: phải mở **cả hai chiều** riêng |
| Loại rule | **Chỉ Allow** | Allow **và** Deny |
| Thứ tự rule | Không có thứ tự, xét tất cả | Xét theo **số thứ tự tăng dần**, gặp rule khớp đầu tiên là dừng |
| Mặc định (khi tạo mới) | Vào: chặn hết. Ra: cho hết | NACL mặc định của VPC: cho hết hai chiều. NACL tự tạo: chặn hết |
| Nguồn/đích trong rule | CIDR, **SG khác**, prefix list | Chỉ CIDR |
| Dùng thường xuyên | **Có, công cụ chính** | Ít; hàng rào thô (chặn dải IP xấu, cách ly subnet) |

### 8.2 "Stateful" nghĩa là gì

Bạn mở SG của RDS cho vào cổng `3306` từ app. App (cổng nguồn ngẫu nhiên, ví dụ `51234`) gửi tới RDS:`3306`. RDS trả lời tới app:`51234`. Với **SG**, phản hồi này **tự được cho qua** vì SG nhớ kết nối đã được phép. Không cần rule outbound nào cho cổng `51234`.

Với **NACL**, nó không nhớ gì: bạn phải có rule inbound `3306` ở subnet data **và** rule outbound tới dải **ephemeral port** (`1024–65535`) để phản hồi đi được. Quên rule ephemeral là lỗi NACL kinh điển nhất.

### 8.3 Sức mạnh thật của SG: tham chiếu SG khác

Thay vì ghi IP, rule SG có thể nói **"cho phép từ bất kỳ thứ gì gắn SG X"**:

| SG | Inbound | Từ nguồn |
|---|---|---|
| `sg-alb` | TCP 443, 80 | `0.0.0.0/0` |
| `sg-app` | TCP 8080 | **`sg-alb`** |
| `sg-db` | TCP 3306 | **`sg-app`** |
| `sg-redis` | TCP 6379 | **`sg-app`** |

Lợi ích: app scale từ 2 lên 20 task, IP đổi liên tục, rule **không phải sửa**. Database chỉ nhận kết nối từ thứ gắn `sg-app`; ngay cả máy khác trong cùng VPC cũng không vào được. Đây là **chuỗi tin cậy** (ALB → App → DB), cách làm chuẩn trên AWS.

### 8.4 Khuyến nghị

- Dùng **SG làm lớp chính**, để NACL ở mặc định (cho hết) trừ khi có yêu cầu cụ thể.
- **Không bao giờ** mở `22` (SSH), `3389` (RDP), `3306`, `6379` cho `0.0.0.0/0`. Bot quét toàn bộ dải IP AWS liên tục, mở là bị dò mật khẩu trong vài phút.
- Đặt tên/mô tả từng rule (`description: "from ALB"`), sau 6 tháng bạn sẽ cảm ơn chính mình.

---

## 9. Load Balancer: ALB, NLB

**Elastic Load Balancing** nhận traffic ở một điểm và phân phối tới nhiều máy phía sau, đồng thời bỏ qua máy không khỏe.

| | ALB (Application) | NLB (Network) | GWLB (Gateway) |
|---|---|---|---|
| Tầng OSI | 7 (HTTP/HTTPS, gRPC, WebSocket) | 4 (TCP/UDP/TLS) | 3 |
| Định tuyến theo | Host, path, header, query (`/api/*` → service A) | Cổng | Gửi qua firewall appliance |
| IP | Đổi theo thời gian, dùng **tên DNS** | Có thể có **IP tĩnh** mỗi AZ | |
| Dùng cho | **Web/API như RoomSplit** | Hiệu năng cực cao, protocol không phải HTTP, cần IP cố định | Hệ thống an ninh mạng |

Các khái niệm của ALB:

```
Listener :443 (HTTPS, chứng chỉ từ ACM)
   └─ Rule: path /api/*  →  Target group "roomsplit-api" (HTTP :8080)
                               ├─ target 10.20.10.5:8080  (healthy)
                               └─ target 10.20.11.7:8080  (unhealthy → không nhận traffic)
Listener :80 (HTTP)  →  redirect sang 443
```

- **Listener**: cổng + protocol ALB lắng nghe.
- **Target group**: nhóm đích (EC2, IP, Lambda, ECS task) kèm **health check**.
- **ACM** (AWS Certificate Manager): chứng chỉ TLS **miễn phí** cho ALB/CloudFront, tự gia hạn. TLS kết thúc ở ALB; ALB → app có thể là HTTP trong mạng private.
- ALB phải được đặt vào **ít nhất 2 public subnet ở 2 AZ** khác nhau.
- ALB gửi header `X-Forwarded-For`, `X-Forwarded-Proto` để app biết IP và scheme gốc của client. ASP.NET Core cần `UseForwardedHeaders()` để đọc chúng.

Health check cho RoomSplit: dự án có `/health/live` (không phụ thuộc gì) và `/health/ready` (kiểm tra DB). Với ECS, target không khỏe sẽ bị **giết và thay mới**. Nếu dùng `/health/ready` mà RDS trục trặc một phút, mọi task bị đánh dấu unhealthy và ECS khởi động lại hàng loạt, làm sự cố tệ hơn. Kinh nghiệm: target group dùng **`/health/live`**, còn `/health/ready` để giám sát và cảnh báo.

---

## 10. RDS và ElastiCache trong VPC

- **DB subnet group**: khi tạo RDS bạn chỉ định một nhóm subnet ở **ít nhất 2 AZ** (dùng `data-a`, `data-b`). Kể cả khi chỉ chạy một instance, AWS vẫn đòi 2 AZ để có chỗ chuyển sang khi bật Multi-AZ.
- **Multi-AZ**: một bản standby đồng bộ ở AZ khác. Khi primary chết, AWS **đổi bản ghi DNS** của endpoint sang standby (khoảng 1–2 phút). Vì vậy app phải kết nối bằng **tên endpoint**, không bằng IP, và phải **retry** (RoomSplit đã có `EnableRetryOnFailure()`).
- `PubliclyAccessible = false`. Luôn luôn, trừ khi đang học và hiểu rõ rủi ro.
- SG `sg-db` chỉ cho `3306` từ `sg-app`. Muốn tự kết nối từ máy dev thì dùng SSM port forwarding (mục 13.3), không mở SG ra Internet.
- **ElastiCache Redis** tương tự: subnet group trong data subnet, SG chỉ cho `6379` từ `sg-app`, bật mã hóa truyền tải và AUTH. Giống Redis trong compose của dự án: không có cổng public.

---

## 11. DNS: Route 53 và DNS trong VPC

### 11.1 DNS bên trong VPC

Mỗi VPC có sẵn **Route 53 Resolver** tại IP **VPC + 2** (với `10.20.0.0/16` là `10.20.0.2`; ngoài ra luôn gọi được qua `169.254.169.253`). Nó phân giải:

- Tên Internet bình thường (`google.com`).
- Tên nội bộ của máy (`ip-10-20-10-5.ap-southeast-1.compute.internal`).
- Endpoint dịch vụ AWS, **trả về IP private** nếu có Interface Endpoint với private DNS (mục 12).
- Bản ghi trong **private hosted zone** gắn với VPC.

### 11.2 Route 53

| Khái niệm | Ý nghĩa |
|---|---|
| **Public hosted zone** | DNS public cho domain của bạn, ví dụ `roomsplit.example.com` |
| **Private hosted zone** | DNS chỉ thấy được bên trong VPC đã gắn, ví dụ `db.roomsplit.internal → endpoint RDS` |
| **Alias record** | Bản ghi đặc thù AWS, trỏ thẳng tới ALB/CloudFront/S3; **miễn phí truy vấn**, dùng được ở **zone apex** (`example.com`, nơi CNAME không được phép) |
| Routing policy | Simple, Weighted (chia % traffic), Latency, Failover (kèm health check), Geolocation |

Luồng phổ biến: `api.roomsplit.example.com` (alias) → ALB → app.

---

## 12. VPC Endpoint: gọi dịch vụ AWS không qua Internet

Mặc định, app trong private subnet gọi S3 hay ECR sẽ đi `app → NAT → IGW → endpoint public của S3`. **VPC Endpoint** cho phép đi đường nội bộ AWS:

| | Gateway Endpoint | Interface Endpoint (PrivateLink) |
|---|---|---|
| Dịch vụ | **Chỉ S3 và DynamoDB** | Hầu hết dịch vụ: ECR, CloudWatch Logs, Secrets Manager, SSM, STS, KMS… |
| Cách hoạt động | Thêm **route** (prefix list → `vpce-`) vào route table | Tạo **ENI có IP private** trong subnet của bạn; DNS của dịch vụ trả về IP này |
| Bảo vệ bằng | Endpoint policy | Endpoint policy **+ Security Group** |
| Chi phí | **Miễn phí** | Khoảng 0,01 USD/giờ mỗi AZ (khoảng 7 USD/tháng mỗi AZ) + phí mỗi GB |

Kinh nghiệm:

- **Luôn tạo Gateway Endpoint cho S3**: miễn phí, giảm phí NAT, không có lý do gì để không làm.
- Interface Endpoint đáng làm khi lưu lượng lớn (pull ECR mỗi lần deploy) hoặc khi cần subnet **hoàn toàn không ra Internet**. ECS Fargate kéo image từ ECR mà không có NAT cần các endpoint: `ecr.api`, `ecr.dkr`, S3 gateway (layer image nằm trên S3), `logs`, và thường thêm `secretsmanager`/`ssm`.

Nối với [iam-notes.md](iam-notes.md): bucket policy có thể dùng Condition `aws:SourceVpce` để **chỉ cho phép truy cập qua endpoint của VPC này**. Lộ credential mà gọi từ ngoài VPC vẫn bị từ chối. IAM và network bảo vệ lẫn nhau.

---

## 13. Kết nối VPC với VPC, với văn phòng, với admin

### 13.1 VPC với VPC

| Cách | Đặc điểm | Hạn chế |
|---|---|---|
| **VPC Peering** | Nối 1–1 hai VPC (cùng hoặc khác tài khoản/region). Không tính phí giờ | **Không bắc cầu**: A↔B, B↔C không có nghĩa A↔C. 10 VPC nối đủ cặp cần 45 peering. CIDR không được trùng |
| **Transit Gateway** | Router trung tâm kiểu hub-and-spoke; mọi VPC/VPN nối vào một chỗ | Tính phí theo giờ mỗi attachment + mỗi GB |
| **PrivateLink** | Chia sẻ **một dịch vụ** (sau NLB) cho VPC khác mà không nối cả mạng | Một chiều, theo từng dịch vụ |

### 13.2 VPC với văn phòng/datacenter

| Cách | Đặc điểm |
|---|---|
| **Site-to-Site VPN** | Đường hầm IPsec qua Internet, dựng trong vài phút, băng thông vài Gbps |
| **Direct Connect** | Đường truyền vật lý riêng tới AWS, ổn định, độ trễ thấp; mất vài tuần để kéo, đắt |
| **Client VPN** | Từng người (laptop) VPN vào VPC (OpenVPN) |

### 13.3 Admin vào máy private thế nào

| Cách | Đánh giá |
|---|---|
| **SSM Session Manager** | **Khuyên dùng.** Không mở cổng 22, không cần public IP, không quản lý SSH key; quyền điều khiển bằng IAM, mọi phiên được ghi log. Máy cần SSM agent + quyền IAM `AmazonSSMManagedInstanceCore` + đường ra SSM (NAT hoặc endpoint `ssm`, `ssmmessages`, `ec2messages`) |
| **SSM port forwarding** | Mở đường tới RDS từ laptop: `aws ssm start-session --target i-xxx --document-name AWS-StartPortForwardingSessionToRemoteHost --parameters host=<rds-endpoint>,portNumber=3306,localPortNumber=13306`, rồi kết nối MySQL vào `localhost:13306` |
| **EC2 Instance Connect Endpoint** | SSH tới máy private không cần bastion, không cần public IP |
| **Bastion host** | Một EC2 trong public subnet mở 22 cho IP của bạn, nhảy qua đó vào máy private. Cách cũ: thêm một máy phải vá, phải giữ key |

---

## 14. Theo dấu một request từ đầu đến cuối

Người dùng mở app, gọi `GET https://api.roomsplit.example.com/api/v1/rooms/1/members`:

```
 1. Trình duyệt hỏi DNS: api.roomsplit.example.com?
      Route 53 (public zone, alias) → trả IP của ALB (ở các public subnet)

 2. Gói tin TCP :443 đi qua Internet → Internet Gateway của VPC
      IGW đổi public IP đích → IP private của node ALB

 3. NACL của public subnet: inbound 443 cho phép? ✔
    SG sg-alb: inbound 443 từ 0.0.0.0/0? ✔

 4. ALB giải mã TLS (chứng chỉ ACM), khớp rule /api/* → target group roomsplit-api
      chọn target khỏe, ví dụ 10.20.10.5:8080
      route table public: 10.20.10.5 khớp 10.20.0.0/16 → local

 5. NACL public outbound + NACL app inbound :8080? ✔
    SG sg-app: inbound 8080 từ sg-alb? ✔

 6. RoomSplit API kiểm tra Redis cache (10.20.20.30:6379)
      SG sg-redis: 6379 từ sg-app? ✔   → cache miss
    Gọi MySQL qua endpoint RDS (DNS nội bộ → 10.20.20.12:3306)
      SG sg-db: 3306 từ sg-app? ✔

 7. Phản hồi đi ngược lại cùng đường.
      SG: stateful, tự cho phản hồi đi.
      NACL: cần outbound tới ephemeral port 1024–65535 ở từng subnet.
```

Và một luồng **đi ra**: lúc deploy, ECS task ở `10.20.10.5` pull image:

```
task → (route 0.0.0.0/0) → NAT Gateway ở public-a → IGW → Internet registry
    hoặc tốt hơn: task → Interface Endpoint ecr.dkr + Gateway Endpoint S3 (không qua NAT)
```

Khi có sự cố, đi đúng theo từng bước này là tìm được chỗ hỏng (mục 18).

---

## 15. So sánh với Docker networking đã học

| Docker (RoomSplit hiện tại) | AWS | Ghi chú |
|---|---|---|
| Bridge network của compose (`roomsplit_default`) | **VPC** | Mạng riêng, cô lập |
| Dải `172.18.0.0/16` Docker tự cấp | **CIDR** của VPC/subnet | Trên AWS bạn tự chọn |
| DNS theo tên service (`mysql`, `redis`) | **Route 53 Resolver**, private hosted zone, endpoint RDS | `Server=mysql` trong compose ≈ `Server=<rds-endpoint>` trên AWS |
| `ports: "8080:8080"` (publish ra host) | **ALB** + SG mở 443 | Cửa từ bên ngoài vào |
| Không publish cổng Redis/MySQL ở prod | Data subnet isolated + SG chỉ cho `sg-app` | Cùng tư tưởng: database không có cửa public |
| Container nào cùng network cũng gọi nhau được | Mọi thứ trong VPC có route `local`, **nhưng SG vẫn chặn nếu không mở** | AWS chặt hơn: mặc định deny |
| `depends_on: service_healthy`, healthcheck | Target group health check | |
| Host là "router" ra Internet (NAT bằng iptables) | **NAT Gateway** / IGW | |

---

## 16. Kiến trúc mạng đề xuất cho RoomSplit

RoomSplit hiện chưa chạy trên AWS. Phần này là bản thiết kế để tham khảo khi đến bước deploy thật (CLAUDE.md, mục P3), **chưa phải quyết định**: khi làm thật sẽ viết tài liệu phương án riêng theo quy trình của dự án.

### 16.1 Sơ đồ

```
Region ap-southeast-1, VPC roomsplit-prod 10.20.0.0/16

                 Route 53: api.roomsplit.example.com (alias)
                                  │
                           Internet Gateway
                                  │
        AZ a                      │                     AZ b
┌──────────────────────┐          │          ┌──────────────────────┐
│ public-a 10.20.0.0/24│◄── ALB (2 AZ) ────► │ public-b 10.20.1.0/24│
│  NAT-a (EIP)         │                     │  NAT-b (EIP)         │
├──────────────────────┤                     ├──────────────────────┤
│ app-a 10.20.10.0/24  │                     │ app-b 10.20.11.0/24  │
│  ECS task :8080      │                     │  ECS task :8080      │
├──────────────────────┤                     ├──────────────────────┤
│ data-a 10.20.20.0/24 │                     │ data-b 10.20.21.0/24 │
│  RDS primary         │ ── đồng bộ ───────► │  RDS standby         │
│  Redis               │                     │  (Redis replica)     │
└──────────────────────┘                     └──────────────────────┘
  + Gateway Endpoint S3 (gắn vào route table app-a, app-b)
```

### 16.2 Route table

| Route table | Gắn vào | Routes |
|---|---|---|
| `rt-public` | public-a, public-b | `local`; `0.0.0.0/0 → igw` |
| `rt-app-a` | app-a | `local`; `0.0.0.0/0 → nat-a`; S3 prefix list `→ vpce-s3` |
| `rt-app-b` | app-b | `local`; `0.0.0.0/0 → nat-b`; S3 prefix list `→ vpce-s3` |
| `rt-data` (đặt làm main) | data-a, data-b | `local` |

### 16.3 Security Group

| SG | Inbound | Outbound |
|---|---|---|
| `roomsplit-alb` | 443, 80 từ `0.0.0.0/0` | 8080 tới `roomsplit-app` |
| `roomsplit-app` | 8080 từ `roomsplit-alb` | 3306 tới `roomsplit-db`; 6379 tới `roomsplit-redis`; 443 tới `0.0.0.0/0` (gọi API AWS, pull image) |
| `roomsplit-db` | 3306 từ `roomsplit-app` | (không cần) |
| `roomsplit-redis` | 6379 từ `roomsplit-app` | (không cần) |

Cổng 8080 khớp với `Dockerfile` hiện tại (image lắng nghe 8080, user non-root).

### 16.4 Ánh xạ cấu hình dự án

| Hiện tại (compose) | Trên AWS |
|---|---|
| `ConnectionStrings__DefaultConnection` với `Server=mysql` | `Server=<rds-endpoint>`, mật khẩu lấy từ **Secrets Manager** đưa vào biến môi trường của ECS task |
| `ConnectionStrings__Redis` với `redis:6379` | Endpoint ElastiCache, TLS + AUTH |
| `Swagger:Enabled` | Giữ như staging/prod hiện tại |
| Migration bằng `dotnet ef migrations script` + `exec mysql` | Chạy từ một task một lần trong app subnet (hoặc qua SSM port forwarding), vì RDS không có cửa public |

### 16.5 Bản tiết kiệm cho môi trường học/staging

Bản đầy đủ có 2 NAT + ALB + RDS Multi-AZ, khoảng trên 100 USD/tháng trước khi có người dùng. Để học:

- **Một AZ** cho NAT (hoặc bỏ NAT): ECS task đặt trong **public subnet** với public IP, SG `roomsplit-app` chỉ cho 8080 từ `roomsplit-alb`. Vẫn an toàn ở mức chấp nhận được vì SG chặn mọi thứ khác, nhưng mỗi task tốn một public IPv4.
- RDS **Single-AZ**, instance nhỏ nhất, vẫn ở data subnet.
- Gateway Endpoint S3 (miễn phí).
- Ghi rõ đây là đánh đổi và **không** dùng cho production.

---

## 17. Lab: dựng một VPC bằng CLI

Mục đích: tự tay thấy "subnet public là do route table". Tất cả tài nguyên trong lab này (VPC, subnet, IGW, route table, SG) **miễn phí**. Lab không tạo NAT, EC2 hay EIP.

Profile `roomsplit-s3-read` ở bài IAM **không có quyền EC2**; dùng profile quản trị (trên máy này là `default`) hoặc tạo role `roomsplit-network-lab` với quyền `ec2:*` cho VPC (group `roomsplit-dev` đã được phép assume `role/roomsplit-*`).

```powershell
$P = "default"; $R = "ap-southeast-1"
$common = @("--profile", $P, "--region", $R)

# 1. VPC + bật DNS hostname
$Vpc = aws ec2 create-vpc --cidr-block 10.20.0.0/16 `
  --tag-specifications 'ResourceType=vpc,Tags=[{Key=Name,Value=roomsplit-lab}]' `
  --query Vpc.VpcId --output text @common
aws ec2 modify-vpc-attribute --vpc-id $Vpc --enable-dns-hostnames @common

# 2. Hai subnet cùng AZ a
$Pub = aws ec2 create-subnet --vpc-id $Vpc --cidr-block 10.20.0.0/24 `
  --availability-zone "${R}a" --query Subnet.SubnetId --output text @common
$App = aws ec2 create-subnet --vpc-id $Vpc --cidr-block 10.20.10.0/24 `
  --availability-zone "${R}a" --query Subnet.SubnetId --output text @common

# 3. Internet Gateway, gắn vào VPC
$Igw = aws ec2 create-internet-gateway --query InternetGateway.InternetGatewayId --output text @common
aws ec2 attach-internet-gateway --internet-gateway-id $Igw --vpc-id $Vpc @common

# 4. Route table public: thêm 0.0.0.0/0 → IGW, gắn vào subnet Pub
$Rt = aws ec2 create-route-table --vpc-id $Vpc --query RouteTable.RouteTableId --output text @common
aws ec2 create-route --route-table-id $Rt --destination-cidr-block 0.0.0.0/0 --gateway-id $Igw @common
aws ec2 associate-route-table --route-table-id $Rt --subnet-id $Pub @common

# 5. Quan sát: subnet nào đang dùng route table nào?
aws ec2 describe-route-tables --filters "Name=vpc-id,Values=$Vpc" `
  --query "RouteTables[].{Id:RouteTableId,Main:Associations[0].Main,Subnets:Associations[].SubnetId,Routes:Routes[].[DestinationCidrBlock,GatewayId]}" @common

# 6. Chuỗi SG: alb → app (tham chiếu SG, không dùng IP)
$SgAlb = aws ec2 create-security-group --group-name roomsplit-lab-alb --description "ALB" `
  --vpc-id $Vpc --query GroupId --output text @common
$SgApp = aws ec2 create-security-group --group-name roomsplit-lab-app --description "App" `
  --vpc-id $Vpc --query GroupId --output text @common
aws ec2 authorize-security-group-ingress --group-id $SgAlb --protocol tcp --port 443 --cidr 0.0.0.0/0 @common
aws ec2 authorize-security-group-ingress --group-id $SgApp --protocol tcp --port 8080 --source-group $SgAlb @common
```

Điều cần quan sát ở bước 5: `$Pub` được gắn với route table có `0.0.0.0/0 → igw` nên là **public**; `$App` không được gắn gì nên dùng **main route table** (chỉ có `local`) nên là **isolated**. Hai subnet được tạo bằng **cùng một lệnh**, chỉ khác route table.

Dọn dẹp (thứ tự ngược lại):

```powershell
aws ec2 delete-security-group --group-id $SgApp @common
aws ec2 delete-security-group --group-id $SgAlb @common
$Assoc = aws ec2 describe-route-tables --route-table-ids $Rt `
  --query "RouteTables[0].Associations[0].RouteTableAssociationId" --output text @common
aws ec2 disassociate-route-table --association-id $Assoc @common
aws ec2 delete-route-table --route-table-id $Rt @common
aws ec2 detach-internet-gateway --internet-gateway-id $Igw --vpc-id $Vpc @common
aws ec2 delete-internet-gateway --internet-gateway-id $Igw @common
aws ec2 delete-subnet --subnet-id $Pub @common
aws ec2 delete-subnet --subnet-id $App @common
aws ec2 delete-vpc --vpc-id $Vpc @common
```

> 💡 Trên Console, mục **VPC → Your VPCs → Resource map** vẽ sẵn sơ đồ subnet ↔ route table ↔ gateway. Mở ra sau bước 5 để đối chiếu với sơ đồ ở mục 1.
>
> Khi đã hiểu, bước tiếp theo là viết hạ tầng bằng code (Terraform / CDK / CloudFormation), không bấm tay hay chạy lệnh rời rạc cho môi trường thật.

---

## 18. Quan sát và gỡ lỗi

### 18.1 Công cụ

| Công cụ | Dùng để |
|---|---|
| **Reachability Analyzer** | Hỏi "từ ENI A tới ENI B cổng 3306 có đi được không?"; AWS phân tích cấu hình (route, SG, NACL) và chỉ ra **đúng thành phần chặn**, không cần gửi gói tin thật |
| **VPC Flow Logs** | Ghi lại metadata mọi kết nối (`srcaddr dstaddr srcport dstport action=ACCEPT/REJECT`) ra CloudWatch Logs hoặc S3. Thấy `REJECT` là biết bị SG/NACL chặn |
| `nc -zv host 3306`, `curl -v` | Thử từ bên trong máy (qua SSM Session Manager) |
| `dig` / `nslookup` | Kiểm tra DNS đang trả IP public hay private |
| ALB target health | Console → Target groups → Targets: lý do unhealthy (timeout, sai mã HTTP) |

### 18.2 Cách đọc triệu chứng

| Triệu chứng | Thường do |
|---|---|
| **Timeout** (treo rồi hết giờ) | **Network**: SG, NACL, route, thiếu public IP, thiếu NAT. Gói tin bị **bỏ im lặng** |
| **Connection refused** | Gói tin **đã tới máy** nhưng không có process lắng nghe cổng đó (app chết, sai cổng, chỉ nghe `127.0.0.1` thay vì `0.0.0.0`) |
| **403 AccessDenied** từ API AWS | **IAM**, không phải network (xem [iam-notes.md](iam-notes.md) mục 8) |
| ALB trả **502** | Target trả lời sai, đóng kết nối, hoặc app crash |
| ALB trả **503** | Không có target nào healthy |
| ALB trả **504** | Target không trả lời kịp: thường là SG app chưa mở cho `sg-alb`, hoặc app treo |
| Private subnet không ra được Internet | Thiếu route `0.0.0.0/0 → NAT`, NAT đặt nhầm vào private subnet, NAT không có EIP, hoặc subnet của NAT thiếu route tới IGW |

Phân biệt **timeout** với **refused** đã loại được một nửa giả thuyết. Timeout thì kiểm tra theo đúng thứ tự gói tin đi ở mục 14: DNS → route → NACL → SG → process.

---

## 19. Chi phí: những thứ hay "đốt tiền" bất ngờ

| Thành phần | Tính phí? | Ghi chú |
|---|---|---|
| VPC, subnet, route table, IGW, SG, NACL | **Miễn phí** | |
| Gateway Endpoint (S3, DynamoDB) | **Miễn phí** | Nên tạo luôn |
| NAT Gateway | Theo giờ + theo GB | Thủ phạm số 1 của hóa đơn dự án nhỏ |
| Public IPv4 / Elastic IP | Theo giờ | Kể cả EIP không gắn vào đâu |
| ALB | Theo giờ + theo LCU (lưu lượng, kết nối) | Khoảng 16–20 USD/tháng khi ít tải |
| Interface Endpoint | Theo giờ mỗi AZ + theo GB | |
| Data transfer | Vào AWS: miễn phí. **Ra Internet**: tính theo GB. **Chéo AZ**: tính cả hai chiều. Cùng AZ qua IP private: miễn phí | Một NAT chung cho 2 AZ thì traffic chéo AZ cũng bị tính |
| Transit Gateway, VPN | Theo giờ mỗi attachment + theo GB | |

Thói quen tốt: bật **AWS Budgets** với ngưỡng cảnh báo (ví dụ 10 USD) ngay khi tạo tài khoản, và dọn tài nguyên lab ngay sau khi học xong.

---

## 20. Lỗi hay gặp và bảng tra cứu nhanh

### 20.1 Lỗi hay gặp

| Lỗi | Hậu quả | Sửa |
|---|---|---|
| Đặt NAT Gateway trong private subnet | Private subnet vẫn không ra Internet | NAT phải ở **public** subnet |
| Đặt app server trong public subnet, mở SG rộng | App lộ trực tiếp ra Internet | App vào private subnet, chỉ ALB ở public |
| SG mở `3306`/`22` cho `0.0.0.0/0` | Bị dò mật khẩu, bị chiếm | Tham chiếu SG; dùng SSM thay SSH |
| Chỉnh NACL mà quên ephemeral port | Kết nối vào được nhưng phản hồi không ra: timeout | Outbound `1024–65535` |
| CIDR VPC trùng mạng nhà/VPC khác | Không peering/VPN được, phải dựng lại VPC | Lên kế hoạch CIDR từ đầu |
| ALB chỉ ở 1 AZ / app chỉ ở 1 AZ | Không tạo được ALB / mất dịch vụ khi AZ lỗi | Tối thiểu 2 AZ |
| App kết nối RDS bằng IP | Failover Multi-AZ đổi IP, app mất kết nối | Dùng tên endpoint + retry |
| Không có Gateway Endpoint S3 | Mọi GB tới S3 trả phí NAT | Tạo endpoint (miễn phí) |
| Nhầm lỗi IAM (403) với lỗi network (timeout) | Debug sai chỗ cả buổi | Xem bảng mục 18.2 |

### 20.2 Lệnh tra cứu

```powershell
aws ec2 describe-vpcs --query "Vpcs[].{Id:VpcId,Cidr:CidrBlock,Default:IsDefault}"
aws ec2 describe-subnets --filters "Name=vpc-id,Values=<vpc>" `
  --query "Subnets[].{Id:SubnetId,Cidr:CidrBlock,Az:AvailabilityZone,PublicIp:MapPublicIpOnLaunch,Free:AvailableIpAddressCount}"
aws ec2 describe-route-tables --filters "Name=vpc-id,Values=<vpc>"
aws ec2 describe-security-groups --group-ids <sg>
aws ec2 describe-network-acls --filters "Name=vpc-id,Values=<vpc>"
aws ec2 describe-nat-gateways
aws ec2 describe-addresses                   # Elastic IP (tìm EIP bỏ quên)
aws ec2 describe-vpc-endpoints
aws ec2 describe-availability-zones --region ap-southeast-1
```

### 20.3 Tóm tắt một dòng

| Khái niệm | Một câu |
|---|---|
| Region / AZ | Vùng địa lý / data center độc lập trong vùng; subnet nằm trong một AZ |
| VPC | Mạng riêng cô lập của bạn trong một region, có dải CIDR |
| Subnet | Một lát của VPC trong một AZ; public hay private do route table |
| Route table | "Đích X thì đi cửa Y"; route cụ thể nhất thắng |
| Internet Gateway | Cửa hai chiều giữa VPC và Internet |
| NAT Gateway | Cửa **một chiều đi ra** cho private subnet; nằm ở public subnet |
| Security Group | Tường lửa từng ENI, stateful, chỉ Allow, tham chiếu SG khác được |
| NACL | Tường lửa cửa subnet, stateless, Allow + Deny, có thứ tự |
| ALB | Load balancer HTTP tầng 7, TLS ở đây, health check target |
| VPC Endpoint | Gọi dịch vụ AWS đường nội bộ; Gateway (S3) miễn phí |
| Route 53 | DNS; alias trỏ thẳng tới ALB; private zone cho tên nội bộ |
| Peering / TGW | Nối VPC: 1–1 không bắc cầu / router trung tâm |
| SSM Session Manager | Vào máy private không cần SSH, không mở cổng |
