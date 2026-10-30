# PowerToys Run Zed Workspaces

一个 PowerToys Run 插件，用来打开 Windows x64 上 Zed Stable 的最近本地项目。只读 Zed 的 SQLite 历史，不常驻后台。

[English](README.md) · [实现细节](documents/implementation.md) · [验证记录](documents/verification.md)

## 环境要求

- PowerToys **0.102.3**（.NET 10 Wox API）
- Windows x64
- Zed Stable
- 构建需要 .NET 10 SDK

其他 PowerToys 或 Zed 版本未验证。

## 构建与安装

```powershell
./scripts/Build.ps1
dotnet test ./tests/ZedWorkspaces.Tests/ZedWorkspaces.Tests.csproj -c Release
```

产物：`artifacts/ZedWorkspaces/` 和 `artifacts/ZedWorkspaces-1.0.0-win-x64.zip`。

先从托盘退出 PowerToys，再执行：

```powershell
./scripts/Install.ps1
./scripts/Uninstall.ps1   # 卸载；执行前同样退出 PowerToys
```

完成后重新启动 PowerToys。安装位置为 `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\ZedWorkspaces`。

## 使用

- **Alt+Space**，输入 `z `：最近工作区，最新的在前。
- `z 名称`：按名称或路径搜索，不区分大小写。优先级依次为名称完全匹配、名称前缀、名称子串、仅路径匹配；同级按最近打开时间排序。
- **Enter**：用 Zed 打开工作区。
- **Ctrl+C** / **Ctrl+Shift+F** / **Ctrl+Shift+C**：复制路径、用资源管理器打开、在命令提示符中打开。

多根工作区会一次打开全部根目录。三个右键操作只针对第一个目录；标题列出所有根目录名称，副标题列出全部完整路径。

查询触发刷新，最快每两秒一次。Zed 更新历史后无需重启插件。

## 限制

仅支持本地 Zed Stable。不支持 Preview/Dev、WSL、SSH、远程项目和自定义用户数据目录。数据库布局与 Zed 的 `WorkspaceDb` 迁移 41 绑定；遇到未知 Schema 时返回空结果并记录诊断，不猜测字段。存储契约和故障处理见[实现细节](documents/implementation.md)。

插件错误与诊断写入 PowerToys Run 的常规日志：`%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Logs`。

## 许可证

MIT。上下文菜单的交互方式参考 Microsoft 以 MIT 许可发布的 VS Code Workspaces 插件。详见 `LICENSE`。
