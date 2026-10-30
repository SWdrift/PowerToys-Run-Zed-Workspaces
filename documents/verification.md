# 验证与迭代记录

## 迭代记录

| 日期 | 内容 |
| --- | --- |
| 2026-10-30 | 对照官方 VS Code Workspaces 插件和 Zed 源码完成初版实现 |
| 2026-10-30 | 增加临时 SQLite 测试，覆盖 WAL、排序、去重、畸形数据和 Schema 兼容性 |
| 2026-10-30 | 构建 Release 包，安装到本机 PowerToys 并完成界面、启动和快捷操作验证 |
| 2026-10-30 | 增加中文 README |
| 2026-10-30 | 精简两份 README；实现细节与验证记录移入 `documents/` |

## 自动化测试

命令：

```powershell
dotnet test ./tests/ZedWorkspaces.Tests/ZedWorkspaces.Tests.csproj -c Release
```

结果：**9 项通过，0 失败**。

| 覆盖点 | 验证内容 |
| --- | --- |
| WAL 与快照 | 有活跃写入方时读取已提交数据；未提交事务不可见 |
| 排序 | 最近打开时间倒序 |
| 去重 | 大小写/尾部分隔符不同但根集合相同的记录合并，保留最新 |
| 同名与重叠 | 同名不同路径、部分重叠的根集合保持独立 |
| 多根顺序 | 二根、三根工作区的逆排列还原 |
| 文件根 | 文件与目录共存时保留文件；纯文件工作区排除 |
| 中文与大小写 | `中文`、混合大小写查询 |
| 无效数据 | 缺失根、非法时间、非法排列、远程记录、WSL 路径 |
| 数据库异常 | 数据库缺失、列类型错误、未来迁移版本 |
| 缓存 | 刷新间隔内不重扫，新实例可读到变化 |
| 参数传递 | 真实子进程接收含中文、空格和 shell 元字符的多个根路径 |

测试全部使用临时目录和临时数据库，不写入真实 Zed 数据。

## 本机实测

环境：PowerToys 0.102.3（.NET 10），Zed Stable 1.26.2，Windows 11 x64。

| 场景 | 结果 |
| --- | --- |
| 插件发现与加载 | 宿主日志记录 `<Zed Workspaces>` 加载和初始化成功 |
| `z ` | 显示最近工作区，按时间倒序 |
| `z LoCuS` | 大小写混合查询命中名称与路径 |
| `z 中文` | 命中含中文的多根工作区 |
| Enter | Zed 打开选中项目 |
| Ctrl+C | 剪贴板得到主目录 |
| Ctrl+Shift+F | Explorer 定位到主目录 |
| Ctrl+Shift+C | 命令提示符工作目录为主目录 |
| 多根启动 | 两个含中文、空格、`&`、`%`、`#`、单引号的根作为同一工作区打开 |
| 历史刷新 | 关闭 Zed 再打开另一项目后，新查询反映最新顺序，无需重启 PowerToys |
| 安装/卸载脚本 | 均成功执行，重装后插件正常加载 |

界面验证使用真实 PowerToys Run 窗口完成，不只检查编译结果。

## 构建与产物

```powershell
./scripts/Build.ps1
```

生成：

- `artifacts/ZedWorkspaces/`：可直接复制的插件目录。
- `artifacts/ZedWorkspaces-1.0.0-win-x64.zip`：分发包，包含 SQLite 托管与本地依赖，不包含 Wox 程序集和 .NET 运行时。

安装位置：

```
%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\ZedWorkspaces
```

