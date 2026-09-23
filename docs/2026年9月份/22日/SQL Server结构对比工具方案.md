# SQL Server 结构对比工具方案

> 创建：2026-09-22 ｜ 当前版本：v2.18 ｜ 状态：🔨开发中
> 页面/模块：`src/FakeSqlCompare` WPF；真实连接 + DacFx 对比 + 脚本/部署
> 状态说明：已发布 win-x64 单文件 exe（自包含，无需安装 .NET）

---

<a id="sec-1"></a>
## 1. 目标与范围

做一个 **只针对 SQL Server** 的结构对比工具（对标 Redgate SQL Compare / Visual Studio Schema Compare 的核心路径），支持：

1. **连接源库 / 目标库**，抽取架构并计算差异；
2. **查看差异**（对象列表 + 左右 SQL 对照 + 将生成的部署脚本）；
3. **一键更新目标库**（先预览脚本，再确认执行）。

本阶段已从假数据原型转入 **正式代码**：连接真实 SQL Server，用 DacFx 对比结构，可导出脚本并对目标库执行。HTML 线框与连接页「演示数据」入口已删除。

<a id="sec-1-1"></a>
### 1.1 明确不做（MVP）

| 项 | 原因 |
|---|---|
| MySQL / PostgreSQL / Oracle | 用户明确只要 SQL Server |
| 行级数据对比（SQL Data Compare） | 另一条产品线，模型和风险都不同，见 [§10](#sec-10) 的 [M2](#scheme-m2) |
| 自动定时同步、CI 无人值守部署 | 安全风险高，第二期再谈 |
| 权限/登录名/作业/SSIS 包 | 对象模型复杂，不进 MVP |

<a id="sec-1-2"></a>
### 1.2 成功标准

- 选两个 SQL Server 库，能在可接受时间内给出 **表 / 视图 / 存储过程 / 函数 / 触发器 / 索引 / 约束** 的差异；
- 每个差异对象能看到 **源定义、目标定义、将执行的 T-SQL**；
- 勾选对象后可 **生成脚本** 或 **对目标库执行**；默认 **不删除** 目标库多出来的对象；
- 执行前有二次确认；失败可看到错误语句与消息。

---

<a id="sec-2"></a>
## 2. 竞品对照（取舍依据）

| 能力 | Redgate SQL Compare | VS / Azure Data Studio Schema Compare | 本工具 MVP |
|---|---|---|---|
| 仅 SQL Server | 是（另有 Data Compare） | 是 | 是 |
| 源/目标：库、dacpac、脚本目录 | 全 | 库 + dacpac | **先做「库 ↔ 库」** |
| 差异列表 + SQL diff | 强 | 够用 | 必须 |
| 勾选后部署 | 有向导 | 有 | 必须 |
| 过滤/忽略选项 | 很细 | 中等 | MVP 做常用忽略项 |
| 许可/售价 | 商业授权 | 免费（VS/ADS） | 内部自用，无授权费 |

结论：交互学 Redgate（清晰、可勾选、可部署），引擎优先复用微软 **DacFx**（与 VS Schema Compare 同源），避免从零解析 `sys` 视图。

---

<a id="sec-3"></a>
## 3. 用户主路径

```
选择源库 ──► 选择目标库 ──► 对比 ──► 浏览/筛选/勾选差异
                                         │
                    ┌────────────────────┼────────────────────┐
                    ▼                    ▼                    ▼
              导出 .sql 脚本      预览部署脚本         一键更新目标库
                                                      （确认 → 执行 → 日志）
```

默认方向：**源 → 目标**（把目标改成与源一致）。不在 MVP 做双向合并。

---

<a id="scheme-p1"></a>
<a id="scheme-p2"></a>
<a id="scheme-p3"></a>
<a id="scheme-e1"></a>
<a id="scheme-e2"></a>
<a id="sec-4"></a>
## 4. 技术方案

<a id="sec-4-1"></a>
### 4.1 产品形态

| 编号 | 形态 | 优点 | 缺点 | 建议 |
|---|---|---|---|---|
| [P1](#scheme-p1) | **WPF 桌面** | 最像传统 DBA 工具，Win 集成好，无浏览器依赖 | 仅 Windows | **已确认** |
| [P2](#scheme-p2) | ~~.NET 8 + Blazor Server 本地 Web~~ | 原型可直接演进；浏览器即用 | 用户明确不做网页 | **已废弃** |
| [P3](#scheme-p3) | Avalonia 桌面 | 可跨 Win/Linux | 只要 SQL Server，跨平台收益小 | 不做首期 |

**已确认：[P1](#scheme-p1) WPF**（2026-09-22）。~~曾建议 [P2](#scheme-p2) Blazor，因「不做网页」废弃。~~ ~~`prototype/index.html` 线框~~ 已删除。正式代码在 `src/FakeSqlCompare`。

<a id="sec-4-2"></a>
### 4.2 对比引擎

| 编号 | 引擎 | 优点 | 缺点 | 建议 |
|---|---|---|---|---|
| [E1](#scheme-e1) | **Microsoft.SqlServer.DacFx** `SchemaComparison` | 官方架构对比；依赖顺序、脚本生成、选项集成熟 | 包体较大；部分边缘对象行为要实测 | **已确认** |
| [E2](#scheme-e2) | SMO + 自研 diff | 对象粒度可控 | 工作量大，易漏依赖/顺序 | 仅当 DacFx 卡死某类对象时补洞 |

**已确认：[E1](#scheme-e1)**（2026-09-22，开始编码）。DacFx 流程：

1. 源/目标各作为一个 `SchemaCompareDatabaseEndpoint`（连接串）；
2. `SchemaComparison.Compare()` 得到 Includeable 结果集；
3. 按用户勾选设置 `Included`；
4. `GenerateScript()` 得到部署 T-SQL；
5. 用户确认后用 `SqlConnection` + `SqlCommand` 分批执行（或 DacFx 发布 API），写执行日志。

<a id="sec-4-3"></a>
### 4.3 逻辑架构（[P1](#scheme-p1) + [E1](#scheme-e1)）

```
WPF 桌面（src/FakeSqlCompare）
    │  连接配置 / 勾选 / 预览 / 确认部署
    ▼
同进程服务层
    ├─ ConnectionService     测试连接、列库名、DPAPI 保存连接
    ├─ CompareService        调 DacFx 对比、缓存本次结果
    ├─ ScriptService         生成完整/勾选后脚本，另存 .sql
    └─ DeployService         二次确认 → 执行 → 进度/错误
              │
              ▼
        SQL Server（源只读抽取，目标可写部署）
```

连接串与密码：**DPAPI（CurrentUser）** 加密后写入本机 `%AppData%/FakeSqlCompare/connections.json`，不进仓库、不进日志。

<a id="sec-4-4"></a>
### 4.4 技术栈（已确认 [P1](#scheme-p1)）

| 层 | 选型 |
|---|---|
| 运行时 | .NET 10（`net10.0-windows`） |
| UI | WPF，主题用 ResourceDictionary（黑暗/明亮） |
| 引擎 | `Microsoft.SqlServer.DacFx` 170 + `Microsoft.Data.SqlClient` 6 |
| 驱动 | `Microsoft.Data.SqlClient`（接真库时） |
| 本地配置 | JSON + DPAPI；主题写入 `%AppData%/FakeSqlCompare/theme.txt` |
| 日志 | 后续 Serilog 写本地滚动文件（脱敏连接串） |

---

<a id="scheme-m1"></a>
<a id="scheme-m2"></a>
<a id="sec-5"></a>
## 5. 核心功能设计

<a id="sec-5-1"></a>
### 5.1 对比对象范围

<a id="scheme-m1-scope"></a>

| 优先级 | 对象 | MVP |
|---|---|---|
| P0 | 表（列、类型、可空、默认、标识列） | ✅ |
| P0 | 主键 / 唯一约束 / 默认约束 / Check | ✅ **写进表脚本**，不单独成行 |
| P0 | 索引（含筛选索引、包含列） | ✅ **写进表脚本**（`CREATE INDEX`），不单独成行 |
| P0 | 外键 | ✅ **写进表脚本**（含 `NOCHECK`），不单独成行 |
| P0 | 视图、存储过程、函数（标量/内联/多语句） | ✅ |
| P0 | DML 触发器 | ✅ |
| P0 | 扩展属性 / 说明（`MS_Description` 等） | ✅ 跟在表/对象脚本后面，不单独成行 |
| P1 | Schema、同义词、序列、用户定义类型 | 第二期 |

[M1](#scheme-m1)：MVP **只做结构对比**。  
[M2](#scheme-m2)：数据行对比 **不做**（废弃进 MVP；若以后要做，另开文档）。

<a id="sec-5-2"></a>
### 5.2 差异状态

| 状态 | 含义 | 默认勾选 | 部署动作 |
|---|---|---|---|
| 相同 | 两边定义一致 | 否（可隐藏） | 无 |
| 不同 | 两边都有，定义不一致 | 是 | `ALTER` / 重建 |
| 仅源 | 目标没有 | 是 | `CREATE` |
| 仅目标 | 源没有 | **否** | `DROP`（破坏性） |

「仅目标」默认不勾选；要删就在列表里勾该对象，确认页再勾「我确认删除」。不再单独放工作台「允许删除」开关。

<a id="sec-5-3"></a>
### 5.3 查看差异

每个对象三个页签：

1. **并排定义**：源 SQL | 目标 SQL，行级新增/删除/变更着色；
2. **将执行脚本**：本次勾选后针对该对象（或全集）的部署 T-SQL；
3. **依赖**：被谁依赖 / 依赖谁（DacFx 已有依赖图，用于排序）。

列表筛选（不做独立搜索框，对象少时类型 + 状态已够用；真库对象很多时再加名称过滤）：

- **类型**：全部 / 表 / 视图 / 存储过程 / 函数；
- **状态**：仅差异 / 含相同 / 已勾选；
- **勾选**：勾选安全差异（不同 + 仅源）、清空勾选。

<a id="scheme-d1"></a>
<a id="scheme-d2"></a>
<a id="sec-5-4"></a>
### 5.4 一键更新（部署安全）

[D1](#scheme-d1) 默认策略：**不勾选 DROP「仅目标」对象**（列表里可手勾）。  
[D2](#scheme-d2) **单页确认**（已确认，不再做 4 步向导、不再要求手输库名）：

1. 同一页展示：创建/修改/删除数量 + 完整脚本 + 可选备份；
2. 有删除（K>0）时必须再勾「我确认删除目标多余对象」，才启用「确认更新」；
3. 按钮执行中禁用，防重复提交；
4. 可选：执行前对目标库 `BACKUP DATABASE`（默认勾选，可取消）；
5. 点确认后进入执行日志：每条成功/失败；失败即停。

~~旧方案：汇总 → 脚本 → 输入目标库名 → 执行。~~ 用户反馈流程过长，已废弃手输库名；破坏性操作改由删除勾选承担。

SQL Server 的 DDL **不能假设整脚本可事务回滚**（不少 `ALTER` 会隐式提交）。产品文案必须写清：**失败不等于全部撤销，请靠备份恢复**。

<a id="sec-5-5"></a>
### 5.5 常用忽略选项（MVP 子集）

- 忽略空格 / 大小写 / 注释（T-SQL `--` 注释，**不含**扩展属性说明）；
- 忽略列顺序；
- 忽略文件组 / 分区方案；
- 忽略权限。

**不忽略：** 默认值、主键、外键、检查/唯一约束、索引、扩展属性（说明）。DacFx 没有「忽略约束名」开关，约束名差异会列出来（比默认 SQL Compare 更严）。

---

<a id="sec-6"></a>
<a id="scheme-theme"></a>
## 6. UI / UX 原则

对标开发者工具，不走消费级花哨风。支持 **黑暗 / 明亮** 两套主题（[D-主题](#scheme-theme)）：顶栏按钮切换；WPF 写入 `%AppData%/FakeSqlCompare/theme.txt`，无记录时跟随 Windows「应用模式」。

原生 `ComboBox` / `CheckBox` / `RadioButton` / `ProgressBar` **必须**用自定义 `ControlTemplate` + `DynamicResource`，否则切主题时仍是系统浅色。状态色用 `DataTrigger` 绑 `DynamicResource`，禁止在 Converter 里缓存 `Brush`（切主题不刷新）。

| Token | 黑暗 | 明亮 | 用途 |
|---|---|---|---|
| Background | `#020617` | `#F8FAFC` | 主背景 |
| Surface | `#0F172A` / `#1E293B` | `#FFFFFF` / `#F1F5F9` | 顶栏、卡片 |
| Border | `#334155` | `#E2E8F0` | 分割线（明亮模式须可见） |
| Text | `#F8FAFC` | `#0F172A` | 主文字，对比 ≥ 4.5:1 |
| Muted | `#94A3B8` | `#475569` | 次要文字 |
| CTA / 新增 | `#22C55E` | `#16A34A` | 对比、部署、diff 新增 |
| 删除 | `#EF4444` | `#DC2626` | 仅目标、DROP、diff 删除 |
| 变更 | `#F59E0B` | `#D97706` | 不同 |
| 字体 | Segoe UI + Consolas | 同左 | 界面 / SQL（WPF 系统字体，不依赖 Web 字体） |

交互约束：

- 可点击元素有 Hover；主题按钮 ≥ 44px；
- 对比、部署超过 300ms 必须有进度，按钮禁用防重复提交；
- 状态不只靠颜色：同时用文字标签「不同 / 仅源 / 仅目标」；
- 图标用矢量 Path，不用 emoji。

---

<a id="sec-7"></a>
## 7. 本地存储（非业务库）

应用自身 **不建 SQL Server 业务表**。本机只存项目/连接：

| 字段 | 类型 | 说明 |
|---|---|---|
| id | GUID | 主键 |
| name | string | **别名**（另存为时手填；保存已有项时保留原名） |
| sourceConnection | 加密 JSON | 服务器、认证、库名 |
| targetConnection | 加密 JSON | 同上 |
| lastComparedAt | datetime | 上次对比时间 |
| options | JSON | 忽略项 |

路径：`%AppData%/FakeSqlCompare/projects.json`。密码用 **DPAPI（CurrentUser）** 加密，不进仓库、不进日志。启动时恢复上次填写的源/目标；下拉显示 **别名 + 服务器.库摘要**；「保存」更新当前项，「另存为」可起别名，「载入」把下拉选中项写回表单（同一项再点一次不会触发选中变更）。无数据库迁移脚本。

---

<a id="sec-8"></a>
## 8. 交互原型

**正式程序：** `src/FakeSqlCompare`（解决方案 `FakeSqlCompare.slnx`）。运行：`dotnet run --project src/FakeSqlCompare`。

~~网页原型 `prototype/index.html`~~ 与连接页「演示数据」入口已删除，不再保留假数据路径。

当前界面覆盖：

| 步骤 | 可点内容 |
|---|---|
| 连接 | 源 / 目标；中间 **⇄ 互换**；测连列库；**保存 / 另存为 / 载入 / 删除**（下拉含连接摘要） |
| 对比中 | 测连源/目标 → DacFx 抽取对比 → 整理差异（可返回取消） |
| 差异工作台 | 表一行；定义脚本含 **CREATE TABLE（主键/外键/默认）+ NOCHECK + CREATE INDEX**；触发器仍单独 |
| 一键更新 | 单页居中；**DacFx 按依赖生成脚本**后执行 GO 批次；可选 `BACKUP`（实例默认备份目录） |
| 主题 | 顶栏切换黑暗/明亮，写入本机配置；下拉/勾选/单选跟主题 |

---

<a id="sec-9"></a>
## 9. 实施路线

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 方案 + WPF 桌面原型（假数据） | ✅ |
| 1 | 真实连接 / 列库 / 测连 | 🔨 本版 |
| 2 | DacFx 对比 + 差异列表 + SQL 预览 | 🔨 本版 |
| 3 | 生成脚本 / 另存 .sql | 🔨 本版 |
| 4 | 部署确认 + 日志 + 可选备份 | 🔨 本版 |
| 5 | 忽略选项 UI、保存项目、取消对比、错误打磨 | 🔨 保存连接 / 重新对比已做；忽略选项仍待做 |

---

<a id="sec-10"></a>
## 10. 待确认决策

确认后把下表「待确认」改成「已确认：xxx（时间）」，被否方案保留并标废弃。

| 编号 | 议题 | 建议 | 状态 |
|---|---|---|---|
| D-形态 | 产品形态 | [P1](#scheme-p1) WPF；~~[P2](#scheme-p2) 网页/Blazor~~ 已废弃 | 已确认：WPF、不做网页（2026-09-22） |
| D-引擎 | 对比引擎 | [E1](#scheme-e1) DacFx；[E2](#scheme-e2) 仅作后备 | 已确认：[E1](#scheme-e1)（2026-09-22） |
| D-范围 | 对比范围 | [M1](#scheme-m1) 仅结构；[M2](#scheme-m2) 数据对比不做 | 已确认：仅结构（2026-09-22） |
| D-删除 | DROP 仅目标对象 | [D1](#scheme-d1) 默认不勾选；确认页再勾一次 | 已确认：去掉工作台「允许删除」开关（2026-09-22） |
| D-确认 | 部署确认 | [D2](#scheme-d2) 单页预览脚本 + 有 DROP 时勾选确认 + 可选备份 | 已确认：不再手输库名、不做多步向导（2026-09-22） |
| D-源类型 | 是否支持 dacpac / 脚本目录 | MVP 只做「库 ↔ 库」 | ⏳待确认 |
| [D-主题](#scheme-theme) | 外观主题 | 黑暗 / 明亮可切换，记忆本机选择 | 已确认：要做（2026-09-22） |

---

<a id="sec-11"></a>
## 11. 风险

| 风险 | 应对 |
|---|---|
| 大库对比慢、锁资源 | 抽取走 DacFx；UI 可取消；提示不要在高峰对生产做对比连接 |
| DDL 失败不可整体回滚 | 文案说明 + 默认建议备份 + 失败即停 |
| 误删目标对象 | [D1](#scheme-d1) 默认不 DROP |
| 连接串泄露 | DPAPI；日志脱敏 |
| DacFx 对某类对象与 SSMS 显示不一致 | 选项对齐 VS Schema Compare；问题对象再考虑 [E2](#scheme-e2) 补洞 |

---

<a id="sec-12"></a>
## 12. 原型实施记录

相对 v1.2 的 UI 反馈：主题漏刷、连接页难看、搜索框无说明、一键更新步骤过多。

| 项 | 实际做法 |
|---|---|
| 主题 | `Themes/Controls.xaml` 为 ComboBox / CheckBox / RadioButton / ProgressBar 写模板；状态色改 DataTrigger |
| 连接页 | 居中双栏「源 → 目标」+ 全宽「开始对比」，去掉大标题营销风 |
| 搜索框 | **去掉**。原框无占位、未真正按名称过滤，易误解；筛选改类型芯片 + 仅差异/含相同/已勾选 |
| 一键更新 | 单页确认（数量、备份、DROP 勾选、脚本）→ 日志；不再手输库名 |
| 布局 | 对照区曾因 DockPanel 默认 Dock=Left 缩在左侧，改为内层 Grid 拉满；更新页 `HorizontalAlignment=Center` Width=880 |

**改动文件：** `MainWindow.xaml`、`MainViewModel.cs`、`Converters.cs`、`App.xaml`、`Themes/Controls.xaml`

**验证：** `dotnet build -c Release` 通过。需关掉当前 `dotnet run` 再启动才能看到新界面。

**偏差：** 连接页密码已改为 `PasswordBox`。忽略选项尚未做独立 UI（对比时写死常用 Ignore*）。项目/连接尚未 DPAPI 落盘。

<a id="sec-13"></a>
## 【v2.18】13. 正式代码实施记录

按方案服务层落地。HTML 线框与 `MockCompareData` 演示路径已删除。

| 模块 | 实现 |
|---|---|
| 连接 | `ConnectionService` 测连列库；打开数据库下拉即列出；`ProjectStore` 保存到 `%AppData%/FakeSqlCompare/projects.json`（UTF-8 直写中文），密码 DPAPI |
| 对比 | 主键/外键/默认/索引并入表脚本；说明拼 `sp_addextendedproperty`；并排定义忽略句尾逗号；**索引按名称配对/排序** |
| 脚本 | 勾选后 `Result.ExcludeAll/Include` + `GenerateScript`（依赖顺序）；导出 `.sql` |
| 部署 | 成功后**自动重新对比**；生成脚本时 Include 子节点和依赖，避免勾了表却没更新列/约束 |
| UI | 连接下拉显示摘要；**载入**；类型芯片为表/视图/过程/函数；对象列表与并排定义之间可拖高度；自定义 `Assets/app.ico` |
| 运行时 | `TargetFramework` 由 `net8.0-windows` 改为 **`net10.0-windows`**（本机 SDK 10.0.401） |
| 打包 | `dotnet publish -p:PublishProfile=win-x64` → `dist/FakeSqlCompare.exe`（win-x64 自包含单文件，约 77MB） |

**验证：** `dotnet build -c Release` 通过。本机需关掉旧进程后启动；真实对比依赖能连上的 SQL Server。

**已知限制：** DacFx 抽取没有细粒度百分比，该步改用滚动条动画。SQL Server 服务账户写不了备份目录时会中止更新。导出的 `.sql` 仍保留 SQLCMD，方便在 SSMS 的 SQLCMD 模式执行。

---

## 迭代记录

| 版本 | 时间 | 变更内容 |
|---|---|---|
| v2.18 | 2026-09-23 10:43 | 发布 win-x64 自包含单文件：`dist/FakeSqlCompare.exe`，对方电脑不用装 .NET |
| v2.17 | 2026-09-23 10:40 | 数据库下拉打开即连接并列库，不必先点「测试连接」；同源账号会同步另一侧列表 |
| v2.16 | 2026-09-23 10:32 | `projects.json` 不再把中文/箭头写成 `\\uXXXX`；启动时若发现转义会自动重写 |
| v2.15 | 2026-09-23 10:25 | 换成自定义应用图标：`Assets/app.ico` 接到 exe、主窗口和另存为窗口 |
| v2.14 | 2026-09-23 10:15 | 并排定义按索引名配对：同一索引因生成顺序不同不再标成差异 |
| v2.13 | 2026-09-22 14:42 | 工作台对象列表与并排定义之间加 GridSplitter，可拖动调整高度 |
| v2.12 | 2026-09-22 14:35 | 并排定义忽略句尾逗号：列定义相同只因后面多一列带逗号时不再标成变更 |
| v2.11 | 2026-09-22 14:28 | 说明（扩展属性）按 SQL Compare 拼进表脚本：`sp_addextendedproperty`，含列级 MS_Description |
| v2.10 | 2026-09-22 14:20 | 表脚本按 SQL Compare：CREATE TABLE 含主键/外键/默认，再接 NOCHECK 与 CREATE INDEX，不再单独拆对象 |
| v2.9 | 2026-09-22 14:10 | 列表展开主键/外键/索引/约束/说明；不再忽略扩展属性；保存后下拉显示连接摘要，并增加载入 |
| v2.8 | 2026-09-22 11:32 | 另存为可起别名；保存不再覆盖别名；连接页与工作台可互换源/目标 |
| v2.7 | 2026-09-22 11:28 | 删除 `prototype/index.html`、连接页演示入口、`MockCompareData` 假数据路径 |
| v2.6 | 2026-09-22 11:26 | 目标框架改为 .NET 10（`net10.0-windows`） |
| v2.5 | 2026-09-22 11:25 | 更新成功后自动重新对比；生成脚本会 Include 表的子对象/依赖，避免勾了表却没改到列 |
| v2.4 | 2026-09-22 11:12 | 进度条补 PART_Track 才能显示百分比；DacFx 抽取步改为不确定动画 |
| v2.3 | 2026-09-22 11:05 | 保存/载入/删除连接（DPAPI）；工作台「重新对比」；对比失败时保留上次结果 |
| v2.2 | 2026-09-22 11:02 | 去掉工作台「允许删除目标多余对象」；[D-删除](#scheme-d1) 改为列表手勾 + 确认页勾选 |
| v2.1 | 2026-09-22 11:00 | 部署去掉 SQLCMD 指令（修 Incorrect syntax near ':'）；失败日志红色，按钮改为「关闭」 |
| v2.0 | 2026-09-22 10:55 | 开始正式代码：SqlClient+DacFx；测连列库、对比、导出、一键更新；确认 [E1](#scheme-e1)/[M1](#scheme-m1) |
| v1.4 | 2026-09-22 10:38 | 对照区拉满底栏；更新页居中，不再贴左 |
| v1.3 | 2026-09-22 10:30 | 主题控件模板；重做连接页；去掉搜索框；一键更新改为单页确认；[D-确认](#scheme-d2) 已确认 |
| v1.2 | 2026-09-22 10:15 | 确认 [P1](#scheme-p1) WPF、废弃网页/Blazor；架构与技术栈改为桌面；开始 `src/FakeSqlCompare` 原型 |
| v1.1 | 2026-09-22 10:10 | 确认黑暗/明亮主题；原型顶栏切换 + localStorage；补 [§6](#sec-6) 双套 token |
| v1.0 | 2026-09-22 10:00 | 初版：范围、P2+E1 建议、差异/部署模型、HTML 原型路径 |
