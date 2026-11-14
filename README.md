# 可能是赌的 · 独立联机服务端

《深空当铺 / Probably Stolen》社区模组“可能是赌的”的开源服务端。提供公共大厅、私人房间、七种牌局、串门、柜台交易，以及账号、日志和钱货暂存。玩家可以连接服主自建的服务器。

客户端兼容：核心 **0.49.0**、联机组件 **0.6.4**。服务端 **0.6.9**，协议版本 1。MIT 许可；依赖许可见 THIRD-PARTY。仓库不包含游戏程序、贴图、音乐、私人账号数据库或正式服务器配置。

## 一键安装

在 [Releases](https://github.com/sx050907-png/probably-stolen-server/releases/latest) 下载。

**Windows x64**：下载 `nightstud-server-installer-windows.zip`，解压后双击 `install.cmd`。安装器下载并校验完整服务端，在当前目录创建 `ProbablyStolenServer`，生成默认配置并启动。也可以执行：

```powershell
.\install.ps1 -InstallDir 'D:\ProbablyStolenServer' -Port 17266
```

**Linux x86_64 / glibc**：下载安装脚本后执行：

```bash
curl -fL https://github.com/sx050907-png/probably-stolen-server/releases/latest/download/install.sh -o install.sh
bash install.sh --dir "$PWD/ProbablyStolenServer" --port 17266
```

完整包自带 .NET 运行时。`--no-start` / `-NoStart` 只安装；私下测试时可以显式加 `--guest-test` / `-GuestTest` 开启访客。访客默认关闭，账号密码和 Steam 登录通过 HTTPS；本机 loopback 允许 HTTP。公网域名、证书和端口放行取决于服主环境，安装器不会修改防火墙或系统服务。

简幻欢 Linux AIO 可以直接上传 Linux 完整包，启动命令为 `bash start.sh`。平台提供的 `SERVER_PORT` 优先于备用 `NIGHTSTUD_PORT`。首次将 `config.env.example` 复制为 `config.env`；示例中的公开域名须换成服主自己的地址。

## 更新与数据

先停止服务器，再运行安装器更新。安装器校验 SHA256，备份旧程序，保留 `config.env`、`data`、证书和现有账号。数据在 `data/accounts.db`；日志在 `logs`。不要把运行目录作为 Git 仓库上传。

```bash
./app/NightStud.Server backup
```

备份命令在线生成 SQLite 一致性备份到 `backups`；另外保管 `config.env`、`data/moderation.json` 和 `data/certificates`。换机器应使用原数据库与配置。不同服务器的账号、余额和物品不自动互通。

## 维护、停服与公告

在安装目录执行；Windows 使用 `app\NightStud.Server.exe`：

```bash
./app/NightStud.Server announce '周六维护预告' '服务器将在 20:00 维护，请先结束牌局并取回暂存。'
./app/NightStud.Server service maintenance '正在维护，预计稍后恢复。'
./app/NightStud.Server service online
./app/NightStud.Server service closed '本服务器已停止联机服务，可选择其他自建服。'
./app/NightStud.Server announce clear
```

这些命令只在服主服务器本地写入 `data/announcement.json` 或 `data/service-status.json`，运行中的服务器约一秒读取。维护时不接新登录、新房间和新准备，已有连接可继续完成牌局、离桌及核对暂存。设置维护提示后再停止进程。进程已完全关闭时无法发送新消息，客户端会显示“暂时无法连接”，并可查看上次保存的公告、手动重试、切换服务器或返回单机。

突然中断的未结算牌局在同一数据库重启后恢复为开局前暂存，未完成押品变为可领取；已提交的结算和回执继续保留，不重复发放。房间不跨进程保存，恢复后重新开房。客户端不凭断线自行退款。永久关服前，应通知玩家结清并取回暂存；原服和数据都丢失时，其他服务器不能代为核实或补发余额。

### 公告的权限边界

- 公告只有 ID、标题、正文、时间四个字段。标题最多 60 字，正文最多 1200 字，客户端与服务端共用校验代码。
- `GET /api/announcements` 只读；没有通过网络发布或修改公告的管理接口。
- 游戏内用普通文字显示，不解释 HTML、富文本或脚本；不打开网址、不下载程序或文件、不修改玩家设置。
- 服务器不能通过公告指定客户端命令、文件路径、按钮或动作。页面固定标明“来自当前服务器服主”。
- 实现可直接审阅：`src/Shared/ServerNotice.cs`、`src/Server/Announcements.cs`、`docs/CLIENT-ANNOUNCEMENTS.cs.txt`。本功能使用现有联机连接，不新增客户端系统权限。

## 从源码构建与验证

需要 .NET 10 SDK 和 Python 3（打包用）。不需要游戏安装目录。

```bash
dotnet build src/Server/NightStud.Server.csproj -c Release
dotnet run --project src/ServiceTests/NightStud.ServiceTests.csproj -c Release -- work/service-test src/Server/bin/Release/net10.0/NightStud.Server.dll
python3 scripts/package.py --output release
```

服务测试会启动隔离的本机 TCP 服务器，验证维护通知、公告只读、重连间隔、重启登录恢复，以及钱货恢复不重复。更多规则、认证和交易测试位于 `src/*Tests`。

CI 在 Windows 与 Linux 验证，标签发布生成两个平台的完整包、一键安装器与 SHA256SUMS。问题反馈时附版本、时间和去除账号口令的日志；不要上传数据库、证书或私人配置。
