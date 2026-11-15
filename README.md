# 可能是赌的 · 独立联机服务端

《深空当铺 / Probably Stolen》社区模组“可能是赌的”的开源服务端。提供公共大厅、私人房间、七种牌局、串门、柜台交易，以及账号、日志和钱货暂存。玩家可以连接服主自建的服务器。

客户端兼容：核心 **0.50.5**、联机组件 **0.7.0**。服务端 **0.7.1**，协议版本 1。MIT 许可；依赖许可见 THIRD-PARTY。仓库不包含游戏程序、贴图、音乐、私人账号数据库或正式服务器配置。

## 0.7.1

新增斗地主牌桌主动短句：七个内置代号、权威发话座位、全桌 8 秒冷却；自动催牌仍为 20 秒等待及 35 秒冷却。升级只替换程序 app，保留 data、config.env、start.sh、日志和证书。新能力 tableVoice 供客户端识别。

## 0.7.0

新增文字电话、私信和离线留言。设置、未读和屏蔽保存在账号数据库中；消息默认保留 30 天，通话仅使用文字。已有账号直接升级，不清库。支持客户端核心 0.50.0 / 联机 0.7.0。

来电仅发送给双方，25 秒无人接听结束；身份、冷却、屏蔽及内容过滤由服务端检查。日志不记录私信正文。联机麻将只传递本人的弃牌听口建议；斗地主趣味语音只接受内置代号，校验当前桌和行动者。

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

## 简幻欢 Linux AIO：统一从 start.sh 启动

简幻欢依靠 `/home/container/start.sh` 启动。服务控制台不能输入 Shell 命令，也不要求用户修改面板启动命令。上面的手动命令适用于其他具有 Shell 的主机。

**首次安装**：在 Releases 下载单独的 `start.sh`，通过文件管理上传到 `/home/container/start.sh`，点击面板“启动”。脚本检测到缺少程序后，会下载安装器、校验完整包并启动服务。GitHub 连通性取决于实例；下载失败会保留现有文件并显示错误，也可以通过文件管理上传 Linux 完整包。

**已有服务器更新**：停服，备份原 `start.sh`，上传新版 `start.sh`；在同目录新建一个空文件 `update.request`，再点击“启动”。更新成功后删除这个标记，再进入正常服务；没有标记且程序已存在时不会访问 GitHub。`data`、`config.env` 和证书保留。平台的 `SERVER_PORT` 优先于备用端口。

**后续版本统一使用 GitHub Releases 更新**：服务端交付包含源代码提交、Linux/Windows 检查、`start.sh` 安装与更新检查，以及带校验文件的版本 Release。确认新版已出现在 Releases 后，已使用本启动器的实例只需停服、新建 `update.request`、点击启动，无需反复上传完整包。新建一次标记只更新一次；发布新版本不会打断正在运行的牌局。

**备份**：停服，在根目录新建空文件 `backup.request` 后点击“启动”。`start.sh` 会先生成 SQLite 一致性备份，成功后删除标记并启动服务；失败时保留标记并显示错误。备份位于 `backups`，还需另行保管配置、词库和证书。

**公告**：通过文件管理将根目录 `announcement.example.json` 复制到 `data/announcement.json`，编辑标题、正文和更新时间。ID 可用 `notice-1` 等字母、数字、短横线组合；更新时间保留示例的完整格式。运行中约一秒读取，不需要输入命令。撤下公告时删除 `data/announcement.json`。

**维护**：将 `service-status.example.json` 复制到 `data/service-status.json`，把 `status` 改为 `maintenance` 并填写 `message`。恢复服务时改回 `online`；正式停止服务的通知使用 `closed`。运行中约一秒读取。维护通知先保存生效，再停止实例。

## 浏览器测试界面与托管范围

网页测试大厅默认关闭，`/`、`/index.html`、`/app.js`、`/style.css` 返回 404。即使旧版网页文件仍在 app/wwwroot 中，也不会开放。游戏客户端使用的账号、房间、牌局、串门、聊天、公告和状态接口继续提供服务；Steam 登录所需的跳转与回调保留。

简幻欢部署保持 `NIGHTSTUD_WEB_TEST_UI=0`（未配置时同样关闭）。其服务清单禁止与游戏服务器无关的通用网站；本程序的游戏联机后端及 Steam 回调是否获准，应向平台确认。关闭网页测试界面不等同于获得平台批准，也不要在实例上另行搭建官网、通用网站或代理服务。

仅在允许网页应用的自建环境排查问题时，可在 config.env 显式设置 `NIGHTSTUD_WEB_TEST_UI=1` 并重启，临时打开浏览器测试大厅。游戏端无需这个设置。公网账号登录仍要求 HTTPS，开关不会降低认证和限流要求。

## 更新与数据

先停止服务器，再运行安装器更新。安装器校验 SHA256，备份旧程序，保留 `config.env`、`data`、证书和现有账号。数据在 `data/accounts.db`；日志在 `logs`。不要把运行目录作为 Git 仓库上传。

```bash
./app/NightStud.Server backup
```

备份命令在线生成 SQLite 一致性备份到 `backups`；另外保管 `config.env`、`data/moderation.json` 和 `data/certificates`。换机器应使用原数据库与配置。不同服务器的账号、余额和物品不自动互通。

## 维护、停服与公告

以下命令只适用于有 Shell 的其他主机，在安装目录执行；Windows 使用 `app\NightStud.Server.exe`。简幻欢请使用上面的文件管理流程：

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

服务测试会启动隔离的本机 TCP 服务器，验证网页默认关闭与显式启用、原生登录及 WebSocket、维护通知、公告只读、重连间隔、重启登录恢复，以及钱货恢复不重复。更多规则、认证和交易测试位于 `src/*Tests`。

CI 在 Windows 与 Linux 验证，标签发布生成两个平台的完整包、一键安装器与 SHA256SUMS。问题反馈时附版本、时间和去除账号口令的日志；不要上传数据库、证书或私人配置。
