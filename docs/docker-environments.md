# Docker cho nhiều môi trường (dev, staging, production): best practice

Tài liệu học tập và phân tích phương án áp dụng cho RoomSplit.

Liên quan: [docker-basics.md](docker-basics.md) (khái niệm), [docker.md](docker.md) (Dockerfile), [docker-compose.md](docker-compose.md) (compose cho dev).

## Mục lục
1. [Ba môi trường để làm gì?](#1-ba-môi-trường-để-làm-gì)
2. [Nguyên tắc số 1: build một lần, triển khai nhiều nơi](#2-nguyên-tắc-số-1-build-một-lần-triển-khai-nhiều-nơi)
3. [Cấu hình khác nhau giữa các môi trường](#3-cấu-hình-khác-nhau-giữa-các-môi-trường)
4. [Quản lý bí mật (secrets)](#4-quản-lý-bí-mật-secrets)
5. [Tổ chức file compose cho nhiều môi trường](#5-tổ-chức-file-compose-cho-nhiều-môi-trường)
6. [Database ở từng môi trường](#6-database-ở-từng-môi-trường)
7. [Migration khi triển khai](#7-migration-khi-triển-khai)
8. [Pipeline CI/CD: từ commit tới production](#8-pipeline-cicd-từ-commit-tới-production)
9. [Gia cố image và container cho production](#9-gia-cố-image-và-container-cho-production)
10. [Chạy container ở đâu?](#10-chạy-container-ở-đâu)
11. [Hiện trạng RoomSplit so với best practice](#11-hiện-trạng-roomsplit-so-với-best-practice)
12. [Phương án áp dụng cho RoomSplit](#12-phương-án-áp-dụng-cho-roomsplit)

---

## 1. Ba môi trường để làm gì?

| Môi trường | Ai dùng | Mục đích | Dữ liệu |
|---|---|---|---|
| **Development (dev)** | Từng lập trình viên, trên máy cá nhân | Viết code, debug, thử nhanh | Dữ liệu giả, xóa thoải mái |
| **Staging** | Cả team, QA, khách hàng duyệt | **Diễn tập production**: kiểm tra bản sắp phát hành trong điều kiện giống thật nhất | Dữ liệu mẫu hoặc bản sao production **đã ẩn thông tin cá nhân** |
| **Production (prod)** | Người dùng thật | Phục vụ thật | Dữ liệu thật, **không được mất** |

> **Ví von:** dev là **phòng tập**, staging là **buổi tổng duyệt** trên sân khấu thật với trang phục thật, production là **đêm diễn**. Tổng duyệt chỉ có giá trị khi nó giống đêm diễn nhất có thể.

Từ đó có nguyên tắc **dev/prod parity** (12-Factor App, mục X): các môi trường càng giống nhau càng tốt. Cùng image, cùng phiên bản MySQL, cùng cách cấu hình. Chỉ khác **giá trị** cấu hình.

---

## 2. Nguyên tắc số 1: build một lần, triển khai nhiều nơi

### ❌ Cách sai: build riêng cho từng môi trường

```
code ──build──▶ image-dev
code ──build──▶ image-staging      ← 3 lần build = 3 image KHÁC NHAU
code ──build──▶ image-prod
```

Mỗi lần build có thể cho ra kết quả khác: NuGet tải bản vá mới, image nền `aspnet:10.0` vừa được cập nhật… Thứ bạn kiểm thử ở staging **không chắc** là thứ chạy ở production.

### ✅ Cách đúng: một image, "thăng cấp" qua các môi trường

```
                         ┌──▶ staging    (ASPNETCORE_ENVIRONMENT=Staging,    DB staging)
code ──build 1 lần──▶ image roomsplit-api:3f2a1c9
                         └──▶ production (ASPNETCORE_ENVIRONMENT=Production, DB prod)
```

- Image là **bất biến** (immutable). Không sửa image sau khi build, không build lại cho production.
- **Chỉ cấu hình khác nhau**, và cấu hình được truyền vào **lúc chạy**: biến môi trường, secrets, file mount.
- Đã kiểm thử image `3f2a1c9` ở staging thì production chạy **đúng từng byte** image đó.

### Đặt tag cho image

| Tag | Dùng khi | Ghi chú |
|---|---|---|
| `latest` | ❌ Không dùng để triển khai | Không biết đang chạy phiên bản nào, không rollback được |
| Git commit SHA, ví dụ `3f2a1c9` | ✅ Mỗi lần build | Truy ngược chính xác về code |
| Semantic version, ví dụ `1.4.0` | ✅ Khi phát hành | Dễ đọc với con người |
| Digest, ví dụ `@sha256:21121642…` | ✅ Chắc chắn nhất | Tag có thể bị ghi đè, digest thì không |

Rollback chỉ đơn giản là **chạy lại tag cũ**.

---

## 3. Cấu hình khác nhau giữa các môi trường

### 3.1. ASP.NET Core đã có sẵn cơ chế theo môi trường

Biến `ASPNETCORE_ENVIRONMENT` quyết định môi trường. ASP.NET Core đọc cấu hình theo **lớp**, lớp sau ghi đè lớp trước:

```
1. appsettings.json                     ← chung cho mọi môi trường
2. appsettings.{Environment}.json       ← ví dụ appsettings.Staging.json
3. User Secrets                          ← chỉ ở Development
4. Biến môi trường                       ← ConnectionStrings__DefaultConnection, ...
5. Tham số dòng lệnh                     ← ưu tiên cao nhất
```

### 3.2. Cái gì để ở đâu?

| Loại cấu hình | Ví dụ | Để ở đâu | Có trong image? |
|---|---|---|---|
| Mặc định chung, không bí mật | `App:TimeZone`, mức log mặc định | `appsettings.json` | ✅ |
| Khác theo môi trường, **không bí mật** | Mức log của staging, bật Swagger ở staging | `appsettings.Staging.json`, `appsettings.Production.json` (commit vào git) | ✅ Chấp nhận được, vì không bí mật |
| **Bí mật** | Password DB, API key | Secret manager → biến môi trường hoặc file | ❌ **Không bao giờ** |
| Phụ thuộc hạ tầng | Hostname DB, URL dịch vụ khác | Biến môi trường | ❌ |

Quy tắc kiểm tra nhanh: **"Nếu repo này public ngay bây giờ, có gì nguy hiểm không?"** Nếu có, thứ đó không được nằm trong git hay image.

### 3.3. Code nên phụ thuộc vào **cấu hình**, không phụ thuộc vào **tên môi trường**

```csharp
// ❌ Gắn chặt với tên môi trường: muốn bật Swagger ở staging phải sửa code
if (app.Environment.IsDevelopment()) { app.UseSwaggerUI(...); }

// ✅ Bật/tắt bằng cấu hình: appsettings.Staging.json đặt "Swagger:Enabled": true
if (app.Configuration.GetValue<bool>("Swagger:Enabled")) { app.UseSwaggerUI(...); }
```

---

## 4. Quản lý bí mật (secrets)

| Môi trường | Cách phổ biến |
|---|---|
| Dev | `.env` (git-ignored), `dotnet user-secrets` |
| CI (GitHub Actions) | **GitHub Secrets / Environments**, đưa vào workflow qua `${{ secrets.X }}` |
| Staging / Prod trên VPS + compose | File secret trên server (quyền `600`), mount bằng **compose `secrets:`** vào `/run/secrets/...` |
| Staging / Prod trên cloud | Azure Key Vault, AWS Secrets Manager, GCP Secret Manager, Kubernetes Secrets |

Một số nguyên tắc:
- **Mỗi môi trường một bộ secret riêng.** Password staging khác password production. Lộ staging không kéo theo lộ production.
- **Không dùng user `root`** của DB ở staging và production. Tạo user riêng cho app, chỉ có quyền trên database `roomsplit`.
- Biến môi trường đọc được qua `docker inspect`. Secret dạng **file** an toàn hơn. ASP.NET Core đọc được bằng `builder.Configuration.AddKeyPerFile("/run/secrets")`.

---

## 5. Tổ chức file compose cho nhiều môi trường

### 5.1. Cơ chế merge nhiều file compose

Compose cho phép **chồng nhiều file**: file sau ghi đè hoặc bổ sung cho file trước.

```powershell
docker compose -f compose.yaml -f compose.prod.yaml up -d
```

Và có một quy ước đặc biệt: nếu **không** truyền `-f`, compose tự đọc `compose.yaml` **và** `compose.override.yaml` (hoặc `docker-compose.yml` và `docker-compose.override.yml`).

### 5.2. Cấu trúc phổ biến

```
compose.yaml            ← phần CHUNG: tên service, image, quan hệ depends_on
compose.override.yaml   ← DEV: tự động được đọc. build từ source, mở cổng DB, Development
compose.prod.yaml       ← STAGING/PROD: phải chỉ định -f. Dùng image từ registry, không mở cổng DB
```

| | Dev (`compose.override.yaml`) | Staging / Prod (`compose.prod.yaml`) |
|---|---|---|
| Image API | `build: .` từ source | `image: ghcr.io/<owner>/roomsplit-api:${TAG}` từ registry, **không build** |
| Cổng MySQL ra ngoài | Mở (`3307:3306`) để dùng DBeaver, `dotnet ef` | **Không mở**. Chỉ API trong network nội bộ truy cập được |
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Staging` / `Production` |
| Secret | `.env` | File secret hoặc secret manager |
| Cổng API | `8080` trực tiếp | Sau **reverse proxy** (Nginx, Traefik, Caddy) lo HTTPS |
| Giới hạn tài nguyên, log rotation | Không cần | `deploy.resources.limits`, `logging.options.max-size` |
| `restart` | `unless-stopped` | `always` hoặc do orchestrator quản lý |

Lệnh chạy:

```powershell
docker compose up -d --build                                                  # dev (tự merge override)
docker compose -f compose.yaml -f compose.prod.yaml --env-file staging.env up -d   # staging
docker compose -f compose.yaml -f compose.prod.yaml --env-file prod.env up -d      # production
```

---

## 6. Database ở từng môi trường

| Môi trường | Best practice |
|---|---|
| Dev | MySQL trong container (như hiện tại). Mất cũng không sao |
| Staging | Database **riêng**, cùng phiên bản MySQL với production. Có thể là container hoặc dịch vụ managed |
| Production | Thường dùng **dịch vụ managed** (AWS RDS, Azure Database for MySQL, Google Cloud SQL): có sẵn backup tự động, failover, vá lỗi bảo mật. Nếu tự chạy trong container thì **bắt buộc** có volume, **backup định kỳ** và **đã thử khôi phục** |

- **Cố định phiên bản**: dùng `mysql:8.0.40` thay cho `mysql:8.0`, để staging và production không lệch phiên bản vì một lần pull khác ngày. Áp dụng tương tự cho image .NET.
- Không bao giờ để staging kết nối nhầm vào DB production. Mỗi môi trường dùng secret riêng nên điều này được đảm bảo một cách tự nhiên.

---

## 7. Migration khi triển khai

| Cách | Dev | Staging / Prod |
|---|---|---|
| `dotnet ef database update` từ máy lập trình viên | ✅ Như hiện tại | ❌ Phụ thuộc máy cá nhân, không truy vết được |
| App tự `Database.Migrate()` lúc khởi động | Chấp nhận được | ❌ Nhiều instance cùng migrate thì tranh chấp. App cần quyền sửa schema |
| **Migration bundle** (`dotnet ef migrations bundle`) chạy như **một bước riêng** của pipeline, hoặc một container `migrator` chạy rồi thoát | ✅ | ✅ **Khuyến nghị** |

Thứ tự triển khai chuẩn: **backup DB → chạy migration → triển khai image mới → kiểm tra `/health/ready`**.

Migration nên **tương thích ngược** (expand/contract): thêm cột mới trước, triển khai code dùng cột mới, rồi mới xóa cột cũ ở lần phát hành sau. Nhờ vậy bản cũ vẫn chạy được trong lúc chuyển đổi, và rollback image không làm hỏng DB.

---

## 8. Pipeline CI/CD: từ commit tới production

```
 Pull request ──▶ CI: format, build, test            (đã có: .github/workflows/ci.yml)
       │
   merge vào main
       ▼
 ① Build image MỘT LẦN, tag = commit SHA
 ② Quét lỗ hổng image (Trivy, Docker Scout)
 ③ Push lên registry (GitHub Container Registry: ghcr.io)
       ▼
 ④ Triển khai STAGING tự động
    └─ migration → chạy image → smoke test /health/ready
       ▼
 ⑤ Người có thẩm quyền DUYỆT (manual approval)
       ▼
 ⑥ Triển khai PRODUCTION với ĐÚNG image đó (cùng SHA hoặc digest)
       ▼
 ⑦ Theo dõi. Có lỗi thì rollback = triển khai lại SHA trước đó
```

GitHub Actions hỗ trợ việc này bằng **Environments** (`staging`, `production`): mỗi environment có bộ secret riêng và có thể yêu cầu người duyệt trước khi chạy job.

---

## 9. Gia cố image và container cho production

| Hạng mục | RoomSplit hiện tại | Best practice cho production |
|---|---|---|
| Chạy non-root | ✅ `USER $APP_UID` | Giữ nguyên |
| Image nền tối giản | `aspnet:10.0` (Ubuntu, có shell) | Cân nhắc `aspnet:10.0-noble-chiseled-extra` (không shell, nhỏ hơn) |
| Cố định phiên bản image nền | `10.0` (trôi theo bản vá) | `10.0.13` hoặc digest, cập nhật có kiểm soát qua Dependabot/Renovate |
| HTTPS | `UseHttpsRedirection` nhưng container chỉ có HTTP | Reverse proxy lo HTTPS. App bật `UseForwardedHeaders` để biết request gốc là HTTPS và IP thật của client |
| Healthcheck | `/health/live`, `/health/ready` đã có | Orchestrator hoặc load balancer gọi các endpoint này |
| Log | Ra stdout (mặc định) | Giữ stdout, thu thập tập trung (Loki, ELK, CloudWatch…). Bật log rotation |
| Filesystem | Ghi được (writable layer) | `read_only: true` + `tmpfs: /tmp` |
| Tài nguyên | Không giới hạn | Giới hạn CPU và RAM để một container không làm sập cả máy |
| Swagger | Chỉ ở Development | Tắt ở production. Staging có thể bật để QA kiểm thử |

---

## 10. Chạy container ở đâu?

| Lựa chọn | Phù hợp | Độ phức tạp | Chi phí |
|---|---|---|---|
| **Một VPS + docker compose** | Dự án nhỏ, team nhỏ, học tập | Thấp | Thấp (vài USD/tháng) |
| **Dịch vụ container managed** (Azure Container Apps, AWS App Runner/ECS, Google Cloud Run) | Muốn tự scale, không quản lý server | Trung bình | Theo mức dùng |
| **Kubernetes** (AKS, EKS, GKE) | Nhiều service, team lớn, yêu cầu cao | Cao | Cao |

Với RoomSplit (một API, một DB, MVP), **VPS + compose** hoặc **container managed** là hợp lý. Kubernetes là quá mức cần thiết.

---

## 11. Hiện trạng RoomSplit so với best practice

| Hạng mục | Hiện trạng | Đạt? |
|---|---|---|
| Image không chứa bí mật | `.dockerignore` loại `.env`, `appsettings.Development.json` | ✅ |
| Cấu hình qua biến môi trường | `ConnectionStrings__DefaultConnection` | ✅ |
| Non-root, health check | Có | ✅ |
| Build một lần, tag theo SHA, push registry | Chưa có. Image chỉ build local, tag `dev` | ❌ |
| File compose theo môi trường | Một file `docker-compose.yml` cho dev | ❌ |
| `appsettings.Staging.json` / `appsettings.Production.json` | Chưa có | ❌ |
| Bật tính năng theo cấu hình thay vì `IsDevelopment()` | Swagger gắn với `IsDevelopment()` | ❌ |
| Migration tách thành bước riêng | Chạy tay từ máy dev | ❌ |
| Reverse proxy, forwarded headers | Chưa có | ❌ |
| User DB riêng, secret riêng mỗi môi trường | Dùng `root` | ❌ (chấp nhận được cho dev) |
| Cố định phiên bản image (`mysql:8.0`, `aspnet:10.0`) | Tag trôi | ⚠️ |
| Nơi triển khai staging / production | Chưa có | ❌ |

---

## 12. Phương án áp dụng cho RoomSplit

Áp dụng toàn bộ mục 11 một lúc là quá nhiều cho một bước học. Dưới đây là ba mức, mỗi mức xây trên mức trước.

### Phương án P1: Mô phỏng nhiều môi trường ngay trên máy local

Nội dung:
- Đổi sang cấu trúc `compose.yaml` (chung) + `compose.override.yaml` (dev, tự đọc) + `compose.prod.yaml` (staging/prod: không mở cổng DB, không build, giới hạn tài nguyên, log rotation).
- Thêm `appsettings.Staging.json`, `appsettings.Production.json` (không bí mật).
- Thay `IsDevelopment()` bằng cờ cấu hình `Swagger:Enabled`.
- Cố định phiên bản `mysql` và image .NET.
- Chạy thử "staging" trên máy bằng `docker compose -f compose.yaml -f compose.prod.yaml --env-file staging.env up`, dùng volume và project name riêng để không đụng dữ liệu dev.

| Ưu điểm | Nhược điểm |
|---|---|
| Học được cơ chế merge compose, cấu hình theo môi trường mà không tốn tiền hay hạ tầng | Không có server thật, nên chưa trải nghiệm triển khai từ xa |
| Thay đổi code nhỏ (`Program.cs` và hai file appsettings) | Đổi tên file compose: README và tài liệu cũ phải sửa theo |

### Phương án P2: P1 + pipeline build và push image lên GHCR

Nội dung thêm:
- Workflow GitHub Actions: khi merge vào `main` thì build image **một lần**, tag theo commit SHA, quét lỗ hổng, push lên `ghcr.io/cuongdo203/roomsplit-api`.
- `compose.prod.yaml` dùng `image: ghcr.io/...:${TAG}`. Chạy "staging" trên máy bằng cách **pull** image từ registry, không build.

| Ưu điểm | Nhược điểm |
|---|---|
| Thực hành đúng nguyên tắc "build một lần" và registry | Image public hoặc private trên GHCR: cần quyết định. Push lên GitHub là thao tác ra bên ngoài |
| GHCR miễn phí cho repo public, tích hợp sẵn với GitHub Actions | Thêm một workflow cần bảo trì |

### Phương án P3: P2 + triển khai thật lên server

Nội dung thêm:
- Một VPS (hoặc Azure Container Apps), reverse proxy với HTTPS, `UseForwardedHeaders`.
- Migration bundle chạy trong pipeline.
- GitHub Environments `staging`/`production` với secret riêng và bước duyệt thủ công.

| Ưu điểm | Nhược điểm |
|---|---|
| Trải nghiệm đầy đủ vòng đời triển khai | **Tốn chi phí** server và tên miền, cần quản trị server |
| | Nhiều khái niệm mới cùng lúc (SSH deploy, TLS, DNS) |

### Đề xuất

**P1 trước, sau đó P2.** P3 để khi có server thật.

- P1 dạy phần **cốt lõi** của bài toán nhiều môi trường: cùng một image, khác cấu hình, và cách tổ chức file compose. Tất cả chạy được trên máy hiện tại, không tốn tiền.
- P2 bổ sung mảnh còn thiếu quan trọng nhất của "build một lần, triển khai nhiều nơi", tận dụng GitHub Actions đã có.
- P3 phụ thuộc hạ tầng bên ngoài và chi phí, không phù hợp làm ngay ở giai đoạn MVP.

Trước khi triển khai P1 sẽ có tài liệu thiết kế chi tiết riêng (danh sách file, nội dung từng file compose, các bước kiểm tra), như đã làm với [docker.md](docker.md) và [docker-compose.md](docker-compose.md).

> **Trạng thái:** đã chọn và triển khai **P1** (2026-10-21), xem [docker-multi-env.md](docker-multi-env.md). P2 và P3 chưa làm.
