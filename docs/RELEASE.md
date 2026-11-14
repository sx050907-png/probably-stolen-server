服务端 0.6.11，兼容核心 0.49.0 / 联机组件 0.6.4。

简幻欢统一使用 start.sh：首次启动自动下载完整包；新建 update.request 后点击启动进行更新；backup.request 在启动前生成备份。公告和维护通过文件管理编辑 JSON 文件，不需要控制台命令。

修正 Windows 一键安装器的兼容性，直接使用系统 SHA256 和 ZIP 库，支持缺少额外 PowerShell 校验、解压模块的环境。

- 新增维护与停服状态、在线维护通知，以及本地编辑的只读文字公告。
- 重连逐步退避并在多次失败后暂停；服务端重启后支持用记住登录恢复会话。
- 包含 Linux x86_64 / Windows x64 完整包和一键安装器，自带运行时，下载有 SHA256 校验。
- 更新保留 data、config.env 和证书。更新程序前先停服，操作步骤见 README。
- 客户端不能根据公告执行命令、下载文件、打开链接或修改系统设置。

Linux 下载完整 tar.gz 或 install.sh；Windows 推荐下载 installer-windows.zip 解压后双击 install.cmd。
