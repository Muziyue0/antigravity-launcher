# 开发与构建

使用 Windows x64、.NET Framework 4.8。PowerShell 5.1 或 7 均可；系统 C# 编译器来自 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。源码保持 C# 5，未引入 NuGet、Node 或 Python 构建依赖。构建流程可在不同 Windows 机器复现，但未承诺字节完全相同。

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Audit-Public.ps1
```

输出为 `dist`；构建脚本生成原创 ICO、两个 EXE、当前版本指针和 SHA256SUMS。固定入口校验版本 EXE 的 SHA-256，拒绝跳出版本目录的路径。分发时保留整个目录，不只拷贝一个 EXE。

测试通过 `AGY_LAUNCHER_DATA` 使用随机临时目录和合成回环代理，不连接真实节点、不修改本机设置。测试结果在忽略的 `artifacts/tests`。CLI 本身不在离线测试中运行。

集成测试另外传入已安装且可信的 Mihomo 路径：

```powershell
.\scripts\Test.ps1 -Integration -CorePath 'C:\path\to\trusted\mihomo.exe'
```

它检查两个独立代理控制面、切换隔离、本地 CONNECT 隧道在软切换后保持及临时目录清理。合成候选指向回环废弃端口；不会读取真实订阅。测试用短命进程模拟客户端退出，绝不终止真实客户端。

`--configure` 强制重新打开配置向导；`--show` 只显示窗口；其余默认检测并启动。已运行的实例会接收显示/启动信号，不创建第二套实际客户端。重新配置时先正常退出客户端及启动器。

## 发布前人工检查

- 在全新 Windows 用户环境完成首次配置、取消下载、官方 CLI 自行登录和首次启动。
- 用与桌面相同的模型得到真实 CLI 回复，确认客户端主程序和后台均经选定代理，再完成桌面真实对话。
- 模拟地区错误，验证备用、同端口复验、用户确认重连和失败回退；记录中断风险。
- 检查升级、回退、崩溃、权限不足、重复启动与正常退出清理。
- 检查发布内容与 Git 历史无配置、密码、个人路径或原始输出。

自动 CI 只证明构建与离线断言通过，不替代上述人工验证。当前仓库还没有面向既有个人版本的通用自动升级安装器，勿覆盖正在运行的版本。
