# displayWindowToMac

Biến máy Windows thành **màn hình phụ của Mac** bằng giao thức của [OpenDisplay](https://github.com/peetzweg/opendisplay).
Mac chạy app OpenDisplay (máy gửi), máy Windows chạy app này (máy nhận). Mac sẽ thấy Windows như một màn hình thật:
kéo cửa sổ sang, sắp xếp trong System Settings → Displays.

> Đây là Bước 2 của dự án. Bước 3 (Windows làm máy chính, iPad/Mac làm màn phụ) sẽ làm sau.

## Cài và chạy

1. Lấy file `OpenDisplayReceiver-win-x64.zip`: tải ở tab **Actions** (artifact của lần build mới nhất), hoặc tự build bằng lệnh `dotnet publish` ở cuối trang.
2. Giải nén, chạy `OpenDisplayReceiver.exe`. Bản này đã kèm .NET, không cần cài thêm gì.
3. Lần đầu Windows hỏi **Firewall**: chọn **Allow** (cho phép mạng Private). Nếu lỡ bấm Cancel, vào
   Windows Security → Firewall → Allow an app, tick `OpenDisplayReceiver`.
4. Trên Mac mở OpenDisplay, vào menu **Connection**, chọn tên máy Windows. Hai máy phải chung một mạng Wi‑Fi/LAN.
5. Trên Windows bấm **F11** để toàn màn hình.

| Phím | Tác dụng |
|---|---|
| F11 | Bật/tắt toàn màn hình |
| Esc | Thoát toàn màn hình |
| F1 | Hiện log (đường dẫn file log nằm ở dòng đầu) |
| F5 | Xin Mac gửi lại khung hình đầy đủ (khi hình bị vỡ) |

Chuột của máy Windows cũng điều khiển được Mac: rê chuột để di con trỏ Mac, bấm trái để click, kéo để kéo,
lăn chuột để cuộn (giữ Shift để cuộn ngang).

Cài đặt nằm ở `%LOCALAPPDATA%\OpenDisplayReceiver\settings.json`: đổi `Name` để đổi tên hiện trên Mac,
`OfferHevc: true` để nhận HEVC (nét hơn ở cùng băng thông nhưng tốn CPU hơn khi giải mã).

## Bàn phím Windows gõ sang Mac (KeyBridge)

Giao thức OpenDisplay không có phím, nên Mac cần chạy thêm một app nhỏ là KeyBridge (`mac/KeyBridge`).

1. Trên Mac: `zsh mac/build-mac.sh` (cần Xcode và `brew install xcodegen`), rồi chạy `mac/build/KeyBridge`.
2. Lần đầu macOS hỏi quyền Accessibility: bật cho app đang chạy KeyBridge (vd Terminal) trong
   System Settings > Privacy & Security > Accessibility, rồi chạy lại KeyBridge.
3. KeyBridge in ra mã ghép đôi 6 số. Khi Mac bắt đầu chiếu, app Windows hỏi mã này (F2 để nhập lại).

Phím: Ctrl trái = Command (Ctrl+C/V như Windows), Ctrl phải = Control, Alt = Option, Win = Control.
Đổi `CtrlIsCommand: false` trong settings.json nếu muốn Ctrl là Control. F1, F2, F11, Alt+F4 vẫn thuộc Windows;
Esc gửi sang Mac (thoát fullscreen bằng F11). Gõ tiếng Việt: tắt Unikey/EVKey bên Windows, dùng bộ gõ của Mac.

## Chế độ 99 Hz / 120 Hz

App Windows báo cho Mac tần số quét thật của màn hình (vd 99 Hz). OpenDisplay gốc luôn chạy 60 Hz, nên
`mac/build-mac.sh` build thêm bản OpenDisplay đã vá (`mac/patches/opendisplay-high-refresh.patch`) để màn ảo
trên Mac và luồng video chạy theo tần số đó (30–120 Hz). Chạy bản này bằng `mac/build/opendisplay/run.sh`
(thoát app OpenDisplay thường trước). Bấm F1 bên Windows để xem số fps thật.

## Giới hạn hiện tại

- Chỉ qua mạng (Wi‑Fi/LAN). Chưa chạy qua cáp USB.
- Giải mã bằng CPU (FFmpeg). Màn 1080p chạy tốt; màn 4K có thể tốn CPU.
- Chuột Windows chưa có double‑click và chuột phải (giao thức `touch` của OpenDisplay chỉ có một kiểu click).
- Chưa có âm thanh.

## Cấu trúc code

```
src/OpenDisplay.Core      Giao thức (khung tin, bắt tay, ping, input), giải mã video FFmpeg, Bonjour. Không phụ thuộc UI.
src/OpenDisplay.Windows   App WinForms: hiển thị video, vẽ con trỏ Mac, chuột/cuộn gửi về Mac.
tests/                    Test tự động: một "Mac giả" stream video thật qua TCP tới receiver.
tools/make_fixture.py     Tạo file video mẫu cho test từ ffmpeg.
```

Build tay (cần .NET 8 SDK):

```
dotnet test tests/OpenDisplay.Core.Tests
dotnet run --project src/OpenDisplay.Windows
dotnet publish src/OpenDisplay.Windows -c Release -r win-x64 --self-contained true -o out
```

Trên Linux/macOS vẫn build được (nhờ `EnableWindowsTargeting`) và chạy được test nếu có thư viện FFmpeg 7.1:
`OPENDISPLAY_FFMPEG_DIR=/đường/dẫn/ffmpeg dotnet test tests/OpenDisplay.Core.Tests -p:RuntimeIdentifier=linux-x64`.

`tools/HeadlessReceiver` là receiver chạy trong terminal (không cửa sổ), in ra số khung hình giải mã mỗi giây.
Nó đã được chạy thử với `od-fake-sender` của OpenDisplay: bắt tay pv 3, 293/293 khung hình, đổi độ phân giải giữa chừng đều ổn.

Giao thức tham khảo: [PROTOCOL.md của OpenDisplay](https://github.com/peetzweg/opendisplay/blob/main/PROTOCOL.md) (pv 3).
App này là một receiver độc lập viết theo tài liệu đó, không dùng lại code của OpenDisplay.
