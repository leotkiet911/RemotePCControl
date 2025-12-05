# 🖥️ Remote PC Control - HCMUS Socket Programming Project

## 📋 Mô tả dự án

Ứng dụng điều khiển máy tính từ xa qua mạng LAN sử dụng Socket Programming với C#. Hỗ trợ streaming webcam real-time với tốc độ 15 FPS.

### ✨ Tính năng chính

1. **📱 Quản lý Applications** - List/Start/Stop ứng dụng
2. **⚙️ Quản lý Processes** - List/Start/Stop tiến trình trong Task Manager
3. **📸 Screenshot** - Chụp màn hình từ xa
4. **⌨️ Keylogger** - Ghi lại phím bấm (cho mục đích học tập)
5. **📹 Webcam Streaming** - Live video streaming real-time 15 FPS + Capture snapshots
6. **🔋 System Control** - Shutdown/Restart máy tính

---

## 🏗️ Kiến trúc hệ thống

```
┌─────────────────┐         ┌─────────────────┐         ┌─────────────────┐
│  Web Interface  │◄───────►│  Server (C#)    │◄───────►│ ClientControlled│
│  (ASP.NET Core) │         │  Console App    │         │  (WinForms C#)  │
│  + SignalR      │         │  Port 8888      │         │  + Webcam       │
└─────────────────┘         └─────────────────┘         └─────────────────┘
     Browser                  Forwarding Server           Controlled PC
```

### Protocol giao tiếp

**Length-Prefix Protocol:**
```
[4 bytes: length] + [N bytes: UTF-8 message]
```

**Message formats:**
```
REGISTER_CONTROLLED|<IP>|<PASSWORD>
LOGIN|<IP>|<PASSWORD>
COMMAND|<TARGET_IP>|<COMMAND_NAME>|<PARAMS>
RESPONSE|<SOURCE_IP>|<RESPONSE_TYPE>|<DATA>
```

---

## 📦 Cài đặt

### Yêu cầu hệ thống

- **OS:** Windows 10/11
- **.NET:** .NET 8.0 SDK
- **IDE:** Visual Studio 2022 (hoặc VS Code)
- **Webcam:** Camera thật (không phải camera ảo)

### Bước 1: Clone hoặc tải project

```bash
git clone <repository-url>
cd RemotePCControl
```

### Bước 2: Cài đặt dependencies

**ClientControlled project:**
```bash
cd ClientControlled
dotnet add package AForge.Video --version 2.2.5
dotnet add package AForge.Video.DirectShow --version 2.2.5
```

**WebInterface project:**
```bash
cd WebInterface
# Dependencies đã được cấu hình trong csproj
```

### Bước 3: sử dụng BuildProject để build, public và tạo các shortcut để chạy project

```bash
# Build Server
cd BuildProject
dotnet run
```

---

## 🚀 Chạy ứng dụng

### Step 1: Khởi động Server (bắt buộc)

```bash
cd Server
dotnet run
```

Hoặc chạy file `Server.exe` trong `bin/Debug/net8.0/`

Hoặc chạy shortcut được tạo ra ở `Desktop` hoặc trong folder `Shortcuts`

**Kết quả:**
```
╔══════════════════════════════════════════════════════════╗
║         REMOTE PC CONTROL SERVER v2.0                    ║
║         HCMUS - Socket Programming Project               ║
║         ✓ Webcam Streaming Support                       ║
╚══════════════════════════════════════════════════════════╝

[SERVER] Started on port 8888
[SERVER] Waiting for connections...
```

### Step 2: Khởi động ClientControlled (PC bị điều khiển)

```bash
cd ClientControlled
dotnet run
```

Hoặc chạy file `ClientControlled.exe`

Hoặc chạy shortcut được tạo ra ở `Desktop` hoặc trong folder `Shortcuts`

**Kết quả:**
```
[CLIENT] Connected to server
[INFO] IP: 192.168.1.100
[INFO] Password: 834521
```

**📝 Ghi chú lại IP và Password này để đăng nhập từ Web!**

### Step 3: Khởi động Web Interface

```bash
cd WebInterface
dotnet run
```

Hoặc chạy shortcut được tạo ra ở `Desktop` hoặc trong folder `Shortcuts`

**Kết quả:**
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:7001
      Now listening on: http://localhost:5000
```

### Step 4: Truy cập Web và điều khiển

1. Mở browser: `https://localhost:7001` (ip này có hiển thị trên cửa sổ console của WebInterface)
2. Chọn **"Người điều khiển"**
3. Nhập **IP** và **Password** từ ClientControlled
4. Click **"Kết nối"**
5. Sử dụng các tính năng!

---

## 🎯 Hướng dẫn sử dụng Webcam Streaming

### Bật webcam và streaming

1. Vào tab **📹 Webcam**
2. Click **"Bật Webcam"** → Camera sẽ tự động chọn camera tốt nhất
3. Click **"Live Stream"** → Video hiển thị real-time với FPS counter
4. Click **"Chụp ảnh"** khi đang stream → Ảnh xuất hiện bên dưới
5. Click **"Dừng Stream"** → Video tạm dừng, camera vẫn bật
6. Click **"Tắt Webcam"** → Tắt hoàn toàn camera

### Tính năng Smart Camera Selection

Camera sẽ được chấm điểm và chọn theo thứ tự:
- ✅ Bỏ qua camera ảo: OBS, Snap Camera, DroidCam, ManyCam...
- ✅ Bỏ qua camera IR/Windows Hello
- ✅ Ưu tiên camera có resolution cao (FHD/HD)
- ✅ Ưu tiên frame rate cao (60fps/30fps)
- ✅ Ưu tiên thương hiệu uy tín: Logitech, Microsoft, HP...

**Console logs:**
```
[WEBCAM] Scanning: Integrated Camera
[WEBCAM] -> Score: 50
[WEBCAM] ✓ SELECTED: Integrated Camera (Score: 50)
[WEBCAM] Resolution: 1280x720 @ 30fps
[WEBCAM] Streaming started
[WEBCAM] Streaming: 75 frames sent, 15.2 fps
```

---

## ⚙️ Tùy chỉnh hiệu suất

### Điều chỉnh Frame Rate

**File:** `ClientControlled/ClientControlled.cs`

```csharp
private void StreamWebcamFrames()
{
    // ...
    Thread.Sleep(66);   // 15 FPS (mặc định)
    // Thread.Sleep(33);   // 30 FPS (mượt hơn, nặng hơn)
    // Thread.Sleep(100);  // 10 FPS (nhẹ hơn)
}
```

### Điều chỉnh chất lượng JPEG

```csharp
private string GetWebcamFrame()
{
    // ...
    encoderParams.Param[0] = new EncoderParameter(
        Encoder.Quality, 60L);  // 60% (mặc định)
        // 80L);  // Rõ hơn, nặng hơn ~30%
        // 40L);  // Mờ hơn, nhẹ hơn ~30%
}
```

### Bảng hiệu suất

| Config | Bandwidth | CPU (Client) | FPS | Quality |
|--------|-----------|--------------|-----|---------|
| 15fps, Q60, 720p | 600 KB/s | 8% | 15 | Good ⭐⭐⭐⭐ |
| 15fps, Q60, 1080p | 900 KB/s | 12% | 15 | Great ⭐⭐⭐⭐⭐ |
| 10fps, Q40, 720p | 200 KB/s | 5% | 10 | OK ⭐⭐⭐ |
| 30fps, Q80, 1080p | 3 MB/s | 18% | 30 | Excellent ⭐⭐⭐⭐⭐ |

**💡 Khuyến nghị:** 15fps, Q60, 720p (cân bằng tốt nhất cho LAN)

---

## 🐛 Xử lý lỗi thường gặp

### ❌ Lỗi: "No webcam found"

**Nguyên nhân:** Không phát hiện camera  
**Giải pháp:**
1. Kiểm tra Device Manager → Camera có hoạt động?
2. Cài lại driver camera
3. Restart máy

### ❌ Lỗi: "No suitable camera found"

**Nguyên nhân:** Tất cả camera bị blacklist  
**Giải pháp:**
1. Xem console logs → Tìm tên camera thực của bạn
2. Mở `ClientControlled.cs` → Tìm hàm `FindBestCamera()`
3. Xóa tên camera khỏi `blacklist` array

### ❌ Lỗi: "Camera in use by another application"

**Giải pháp:**
1. Đóng tất cả app khác đang dùng camera (Zoom, Teams, Skype...)
2. Mở Task Manager → Tìm process đang giữ camera
3. Restart ClientControlled app

### ❌ Lỗi: Stream lag/chậm

**Giải pháp:**
- Giảm FPS xuống 10 (`Thread.Sleep(100)`)
- Giảm quality xuống 40 (`Encoder.Quality, 40L`)
- Giảm resolution bằng cách giới hạn trong `FindBestCamera()`

### ❌ Lỗi: "Cannot connect to server"

**Giải pháp:**
1. Kiểm tra Server có đang chạy không?
2. Kiểm tra Windows Firewall → Allow port 8888
3. Kiểm tra IP address đúng không (127.0.0.1 cho localhost)

---

## 📊 Testing Checklist

### ✅ Server
- [ ] Server khởi động thành công
- [ ] Console hiển thị "Started on port 8888"
- [ ] Accept được connections

### ✅ ClientControlled
- [ ] Kết nối được Server
- [ ] Hiển thị IP và Password
- [ ] Camera được detect và chọn đúng

### ✅ Web Interface
- [ ] Đăng nhập thành công
- [ ] Tất cả 6 tabs hoạt động
- [ ] List Apps/Processes thành công
- [ ] Screenshot hoạt động
- [ ] Keylogger hoạt động
- [ ] Webcam streaming hoạt động
- [ ] FPS counter hiển thị
- [ ] Capture snapshot hoạt động
- [ ] System commands hoạt động

### ✅ Webcam Streaming
- [ ] Camera khởi động thành công
- [ ] Video stream mượt mà
- [ ] FPS ~15 (±2)
- [ ] LIVE indicator hoạt động
- [ ] Capture snapshot thành công
- [ ] Snapshots hiển thị dưới video
- [ ] Stop/Start stream hoạt động
- [ ] Tắt webcam cleanup đúng

---

## 📁 Cấu trúc Project

```
RemotePCControl/
├── Server/
│   ├── Server.cs                  # Main server forwarding logic
│   └── Server.csproj
│
├── ClientControlled/
│   ├── ClientControlled.cs        # Main client service với webcam
│   ├── Form1.cs                   # WinForms UI
│   ├── Program.cs
│   └── ClientControlled.csproj
│
└── WebInterface/
    ├── wwwroot/
    │   └── index.html             # Web UI với streaming support
    ├── Hubs/
    │   └── ControlHub.cs          # SignalR Hub
    ├── Services/
    │   └── ConnectionService.cs   # Service kết nối Server
    ├── Program.cs
    └── WebInterface.csproj
```

---

## 🎓 Báo cáo Đồ án

### Nội dung báo cáo gợi ý

#### 1. Giới thiệu
- Socket Programming là gì?
- TCP/IP Protocol
- Mục tiêu dự án

#### 2. Phân tích yêu cầu
- 6 tính năng chính
- Use cases
- Kiến trúc Client-Server

#### 3. Thiết kế hệ thống
- Sơ đồ kiến trúc
- Protocol thiết kế
- Length-prefix framing
- Message format

#### 4. Cài đặt
- Server implementation
- Client implementation
- Web Interface với SignalR
- Smart Camera Selection Algorithm
- Webcam Streaming Implementation

#### 5. Testing và Kết quả
- Screenshots các tính năng
- Performance metrics
- Webcam streaming demo

#### 6. Kết luận
- Kết quả đạt được
- Khó khăn và cách giải quyết
- Hướng phát triển

---

## 🔒 Lưu ý bảo mật

**⚠️ QUAN TRỌNG:** Đây là project học tập về Socket Programming.

- ✅ Chỉ sử dụng trong môi trường LAN an toàn
- ✅ Không expose ra Internet
- ✅ Keylogger chỉ cho mục đích demo
- ✅ Không sử dụng cho mục đích xấu

---

## 📝 License

Project này được tạo cho mục đích học tập tại HCMUS.

---

## 👥 Contributors

1. **Họ tên:** Huỳnh Tuấn Kiệt
- **MSSV:** 24120356
- **Lớp:** 24CTT5
- **Môn:** Mạng máy tính
- **Giảng viên:** Đỗ Hoàng Cường

2. **Họ tên:** Võ Nhật Liệu
- **MSSV:** 24120368
- **Lớp:** 24CTT5
- **Môn:** Mạng máy tính
- **Giảng viên:** Đỗ Hoàng Cường

3.  **Họ tên:** Đinh Tiến Phát
- **MSSV:** 24120405
- **Lớp:** 24CTT5
- **Môn:** Mạng máy tính
- **Giảng viên:** Đỗ Hoàng Cường

---

## 📞 Support

Nếu gặp vấn đề, hãy:
1. Xem phần **Xử lý lỗi** ở trên
2. Check console logs (F12 trên browser)
3. Check Server console và ClientControlled console
4. Liên hệ 24120356@student.hcmus.edu.vn để được tư vấn giải quyết

---

## 🎉 Demo Video

[Link video demo của bạn]

---

**🌟 Chúc bạn thành công với đồ án!**
