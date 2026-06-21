# Windows 远程桌面客户端 (mstsc.exe) 使用与配置指南

本指南详细介绍了 Windows 系统远程桌面连接程序 (`mstsc.exe`) 的命令行参数、最佳实践，以及如何通过动态生成 `.rdp` 文件来优化 RPA/自动化场景下的远程登录体验。

---

## 1. `mstsc.exe` 命令行参数

通过命令行启动 `mstsc.exe` 时支持以下开关：

| 参数 | 说明 |
| :--- | :--- |
| `[connection file]` | 指定要加载并启动连接的 `.rdp` 配置文件路径。 |
| `/v:<server[:port]>` | 指定要连接的远程 PC 的 IP 地址或计算机名（及端口号）。 |
| `/f` 或 `/fullscreen` | 在全屏模式下启动远程桌面会话。 |
| `/w:<width>` | 指定远程桌面窗口的宽度（像素）。 |
| `/h:<height>` | 指定远程桌面窗口的高度（像素）。 |
| `/public` | 在公共模式下运行远程桌面（不自动保存密码或首选项）。 |
| `/span` | 使远程桌面的宽度和高度与本地虚拟屏幕匹配（跨多显示器扩展）。 |
| `/multimon` | 配置远程桌面会话，使其监视器布局与当前的本地客户端相同。 |
| `/edit "connection file"` | 打开指定的 `.rdp` 文件进行编辑（不直接发起连接）。 |
| `/prompt` | 在连接时强制提示用户输入凭据（即使已保存凭据）。 |
| `/admin` | 将您连接到用于管理远程服务器的控制台会话（Console）。 |

> [!WARNING]
> **关于用户名 `/u` 参数的遗留与废弃**
> - 在较旧的 Windows 版本（或部分第三方工具）中曾支持通过 `/u:<username>` 命令行参数指定连接的账户。
> - **现代 Windows 系统（如 Win10 / Win11）已彻底移除命令行中的 `/u` 参数。** 如果在命令行传入不被支持的 `/u` 开关，会直接触发 `mstsc.exe` 弹出“用法说明”警告弹窗并阻断连接。
> - **最佳且唯一的替代方案**：通过指定 `.rdp` 文件并在文件中配置 `username:s:<value>` 来实现账号传递。

---

## 2. `.rdp` 配置文件核心属性指南

为了在自动化连接时实现**免密登录**、**静音运行**、**自动缩放**以及**屏蔽证书警告**，可以通过生成临时 `.rdp` 文本文件并配置以下关键键值对：

### 2.1 基础与免密配置
* **`full address:s:<Server>`**
  * 目标 PC 的 IP 地址或计算机名。
* **`username:s:<Username>`**
  * 指定连接的用户名。
* **`prompt for credentials:i:0`**
  * `0`：禁用凭据输入提示，自动读取并使用 Windows 凭据管理器中由 `cmdkey` 注册的匹配密码；
  * `1`：始终提示用户输入密码。
* **`authentication level:i:0`**
  * `0`：如果服务器证书校验失败，**依然继续连接且不显示任何证书警告弹窗**（极其适合 RPA 本地回环 `127.0.0.2` 等自签/无证书场景）；
  * `1`：如果验证失败，则显示警告；
  * `2`：如果验证失败，则拒绝连接。

### 2.2 窗口与显示配置
* **`screen mode id:i:<value>`**
  * `1`：窗口模式（根据 `desktopwidth` 和 `desktopheight` 的值确定尺寸）；
  * `2`：全屏模式。
* **`desktopwidth:i:<width>`**
  * 窗口宽度（像素）。
* **`desktopheight:i:<height>`**
  * 窗口高度（像素）。
* **`smart sizing:i:<value>`**
  * `1`：启用分辨率自适应缩放（拖拽 `mstsc` 窗口大小时，内部远程桌面画面会按比例自适应缩放，防止出现滚动条）；
  * `0`：禁用自适应。

### 2.3 资源重定向与性能优化
* **`audiomode:i:2`**
  * 远程声音重定向模式。`2` 表示**“不要播放远程音频”**（在本地静音，可大幅节省网络带宽与目标系统的 CPU/内存开销）。
* **`redirectclipboard:i:1`**
  * `1`：启用双向剪贴板共享；
  * `0`：禁用。
* **`redirectdrives:i:0`**
  * `1`：重定向本地驱动器（磁盘）；
  * `0`：禁用。
* **`redirectprinters:i:0`**
  * `0`：禁用本地打印机重定向（缩短连接建立耗时）。
* **`redirectcomports:i:0`**
  * `0`：禁用串口重定向。

---

## 3. RPA 场景下的黄金组合实现逻辑

在 RPA 隔离环境或多会话管理中，要实现**100% 自动且无弹窗的远程桌面调起**，最佳调用链路如下：

1. **注册凭据**：通过命令行无窗静默调用 `cmdkey.exe /generic:TERMSRV/{Server} /user:{Username} /pass:{Password}`。
2. **生成 RDP 配置**：在临时目录下生成一个自定义的 `.rdp` 文件，写入：
   ```ini
   full address:s:{Server}
   username:s:{Username}
   prompt for credentials:i:0
   authentication level:i:0
   smart sizing:i:1
   audiomode:i:2
   ```
3. **启动客户端**：调用 `mstsc.exe "{tempRdpPath}"` 发起连接。
4. **清理垃圾**：在启动后延迟若干秒（例如 10 秒），将该临时 `.rdp` 文件删除。
