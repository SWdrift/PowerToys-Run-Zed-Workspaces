# 实现细节

本文记录 README 之外的数据库契约、排序规则和故障处理。路径统一用环境变量表示，不包含开发机信息。

## 组成

| 文件 | 职责 |
| --- | --- |
| `Main.cs` | PowerToys Run 入口、结果生成、启动、上下文菜单 |
| `ZedWorkspacesApi.cs` | 只读 SQLite、校验、去重、搜索、快照 |
| `ZedWorkspace.cs` | 工作区模型：完整根目录集合、主目录、显示文本、去重标识 |
| `ZedInstance.cs` | Zed Stable 可执行文件发现、`ProcessStartInfo` 构造 |
| `plugin.json` | 插件 ID、触发词、入口 DLL、图标 |

插件只做 PowerToys 接入和本地项目读取，没有后台进程、索引服务或编辑器抽象层。

## 数据库契约

只读取：

```
%LOCALAPPDATA%\Zed\db\0-stable\db.sqlite
```

以 Zed 提交 `c4273271092b0287f7d181f616bd5171e0a2f70c`（Stable 1.26.2）核实：

- `PRAGMA user_version = 0`。
- `migrations(domain TEXT, step INTEGER, migration TEXT)` 中 `WorkspaceDb` 的最大 step 为 **41**。
- `workspaces`：
  - `workspace_id INTEGER PRIMARY KEY`
  - `paths TEXT`、`paths_order TEXT`
  - `remote_connection_id INTEGER`
  - `timestamp TEXT`
  - `identity_paths TEXT`、`identity_paths_order TEXT`（同一迁移加入）
- `paths` 是以换行分隔的完整路径，按字典序存储。`paths_order` 是逗号分隔的原始索引；必须求逆排列才能还原显示顺序，直接按 `paths_order[i]` 取 `paths[i]` 会得到错误顺序。
- `timestamp` 为 UTC `yyyy-MM-dd HH:mm:ss`；最近历史按时间倒序。
- 数据库运行在 WAL 模式。

读取使用 `SqliteOpenMode.ReadOnly`、`Pooling=false`，并在延迟读取事务中完成元数据检查和数据查询。这样能读到已提交的 WAL 内容，同时保持单一快照。没有使用 `immutable=1`，因为它可能忽略活跃 WAL。插件不执行写入、迁移、checkpoint 或清理，也不修改 Zed 配置。

## 兼容性检查

读取前同时验证：

1. `user_version` 必须为 0。
2. `WorkspaceDb` 迁移 step 必须等于 41（更新或更旧都拒绝）。
3. `migrations` 和 `workspaces` 的列名及 SQLite 声明类型符合预期，`workspace_id` 必须是主键。

任一项不满足就返回空结果并记录诊断。数据库缺失、被占用、无法打开或读取异常也走同一路径。已缓存的旧结果会在下一次刷新时被替换，避免 Schema 变化后继续展示过期数据。

## 记录解析与去重

逐行处理，遇到以下情况跳过该工作区：

- `paths` 为空或为 NULL（空白/临时工作区）。
- 有 `remote_connection_id`（远程工作区）。
- 路径不是绝对盘符路径或合法 UNC 路径。
- 路径位于 `\\wsl$`、`\\wsl.localhost` 或 `\\.\` 设备命名空间。
- 时间戳格式不符。
- `paths_order` 不是完整、无重复的索引排列。
- 任一根本身不存在或不可访问。
- 所有根都是文件，没有目录。

只要有一个根缺失，整个工作区就不显示；不会悄悄删掉缺失的根再打开一个不完整项目。文件根可以和其他目录根共存，并在启动时原样传入。

去重键是**忽略大小写、与显示顺序无关的完整根目录集合**。重复记录保留最近打开的一条；时间相同则保留 ID 较大者。名称相同但根集合不同、或者集合有重叠但不相等的条目不会被合并。

`identity_paths` 是 Zed 的 Git worktree 提示，不等于实际重新打开的根目录集合。本插件不使用它合并条目，避免把两个不同的本地项目误判为一个。

## 搜索与排序

空查询按最近打开时间倒序。

非空查询先去除首尾空白，然后按以下等级匹配：

1. 名称完全相等。
2. 名称前缀。
3. 名称子串。
4. 仅完整路径包含查询。

全部使用 `OrdinalIgnoreCase`，因此中文和大小写行为稳定。同一等级按最近打开时间倒序；时间相同再按名称、ID 排序。结果设置 `DisableUsageBasedScoring`，防止 PowerToys 的选择历史打乱已确定的顺序。

多根工作区的名称由各根目录名用 `, ` 连接，副标题是全部完整路径，用 `; ` 连接。

## 快照

`GetWorkspaces()` 使用进程内快照，默认 2 秒内不重复打开数据库。刷新由查询触发，没有计时器或文件监视。打开/关闭 Zed 后，等两秒再查询即可看到新记录。

## 启动

`ZedInstance` 依次查找：

1. `%LOCALAPPDATA%\Programs\Zed\Zed.exe`
2. `%ProgramFiles%\Zed\Zed.exe`
3. `%ProgramFiles(x86)%\Zed\Zed.exe`
4. `App Paths\zed.exe`（HKCU/HKLM，32/64 位视图）
5. `PATH` 中的 `zed.exe`

候选文件必须存在、文件名为 `zed.exe`，且版本信息的产品名为 `Zed`、公司名为 `Zed Industries, Inc.`，以排除 Preview、Dev、Nightly。

启动使用：

```csharp
FileName = <Zed.exe>;
UseShellExecute = false;
ArgumentList.Add(root1);
ArgumentList.Add(root2);
```

Zed 的 CLI 接受多个位置路径，`ArgumentList` 负责转义，因此空格、中文、`&`、`%`、`#`、单引号等字符不需要手工拼接引号。所有根按原始显示顺序传入，作为一个多根工作区打开。

## 上下文菜单

| 快捷键 | 行为 |
| --- | --- |
| Ctrl+C | 复制主目录 |
| Ctrl+Shift+F | 用 Explorer 打开主目录 |
| Ctrl+Shift+C | 在命令提示符（工作目录为主目录）打开 |

主目录是原始顺序中的第一个目录；如果第一个根是文件，则取之后第一个目录。多根工作区的路径操作只针对这一个目录，这是有意定义的行为。Explorer 通过 `ArgumentList` 传路径；控制台使用 PowerToys 的 `Helper.OpenInConsole`，它只把路径作为 `cmd.exe` 的工作目录，不做 shell 拼接。

启动或菜单操作失败时写入 PowerToys 日志，并通过 `ShowMsg` 提示。

## 构建依赖

- 目标框架 `net10.0-windows`，x64。
- 编译期引用已安装 PowerToys 的 `Wox.Plugin.dll`、`Wox.Infrastructure.dll`，标记为 `Private=false`，不复制进插件包。
- 打包 `Microsoft.Data.Sqlite` 及 `e_sqlite3.dll` 等运行时文件。
- 不依赖 PowerToys 仓库的 `RepoRoot` 或内部构建属性。

## 已知限制

- 仅 Windows x64、本地 Zed Stable、迁移 41。
- 不支持 Preview、Dev、WSL、SSH、远程项目、自定义 `--user-data-dir`。
- 不恢复窗格、编辑器标签或会话；打开的是项目根目录。
- 最近历史是 Zed 内部实现，Schema 变化需要重新适配。
- 上下文操作只作用于主目录。
