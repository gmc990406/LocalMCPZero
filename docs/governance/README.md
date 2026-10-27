# ZERO 智能治理中心与 AI 视觉工作台（v1.3.0）

本文档说明 v1.3.0 新增的记忆治理、任务中心和视觉工作台：数据结构、迁移与回滚、MCP 工具、错误码、界面入口、兼容性和已知限制。

## 1. 总览

| 模块 | 作用 | 主要代码 |
|---|---|---|
| 记忆生命周期 | 版本历史、回收站、归档、保护、降权、合并、更正 | `Resources/zero_gov.py` · `Services/MemoryStore.cs` |
| 记忆治理 | 本地规则扫描（不调用模型）→ 候选 → AI 领取任务提交结论 → 低风险自动生效 / 高风险用户审批 | `zero_gov.py` · `GovernanceWindow` |
| 任务中心 | AI 登记多步任务（task_*）、维护任务租约、事件流、严格模式 | `server.py` · `zero_gov.py` · `GovernanceWindow` |
| 视觉工作台 | 窗口列表 / UIA 控件树 / 窗口截图与录制 / 授权后的控件操作 / 受控浏览器 | `Resources/zero_ui.py` · `Services/UiaProvider.cs` · `VisionWindow` |
| 统一迁移 | Python 服务与 WPF 共用 `zero_schema.json`，先备份再在事务中升级 | `Services/SchemaMigrator.cs` · `zero_gov.py` |

设计原则：

- **确定性优先**：记忆检查只用本地规则（规范化文本比较、相似度、未检索天数、长度、过期时间），不调用任何模型，不产生费用。
- **任何修改都可恢复**：每次修改写入 `memory_versions`；删除先进回收站；合并只归档被合并的记忆并记录 `merged_into`。只有在回收站里「彻底删除」才不可恢复（界面二次确认）。
- **AI 提建议，用户做决定**：AI 只能提交结构化结论；按策略只有 `keep`、`lower_priority` 可以自动生效，其余（合并、压缩、更正、归档、删除）必须用户在「治理中心 → 记忆治理」批准。受保护 / 置顶的记忆不会被 AI 结论修改。
- **默认最小权限**：「操作窗口控件」「操作网页」默认关闭；即使开启，每个窗口还要用户在视觉工作台单独限时授权；ZERO 主程序窗口、UAC/登录等安全窗口始终禁止。

## 2. 数据库与迁移

数据目录：`%LOCALAPPDATA%\ZERO\`。

| 数据库 | 版本 | 变化 |
|---|---|---|
| `memory.db` | v1 → **v2** | `memories` 增加 `revision`、`protected`、`archived_at`、`deleted_at`、`priority`、`quality_state`、`expires_at`、`merged_into`（均有默认值）；新增 `memory_versions`、`memory_events`、`memory_review_candidates`、`memory_review_decisions`、`memory_relations`；升级时为每条已有记忆写入基线版本 |
| `governance.db` | 新建 **v1** | `governance_tasks`、`task_events`、`settings`（策略）、`ui_grants`、`ui_requests`、`ui_locks`、`ui_actions`（审计）、`scan_runs` |
| `history.db` | v3（不变） | — |

迁移规则（`Resources/zero_schema.json`，C# 与 Python 两端执行同一份定义）：

1. 打开数据库时读取 `PRAGMA user_version`，低于目标版本才升级；
2. 升级前复制备份为 `<库>.bak-v<旧版本>`（只备份一次，已存在不覆盖）；
3. 在 `BEGIN IMMEDIATE` 事务中按版本依次执行；`ALTER TABLE ADD COLUMN` 遇到 duplicate column 视为已完成（可重入）；任一步失败整体回滚，库保持原样；
4. 两端同时启动时由 SQLite 写锁串行化，后到的一方看到新版本直接跳过。

实测（真实数据副本）：`memory.db` 38 条记忆 v1 → v2，备份 `memory.db.bak-v1` 生成，升级后 38 条、38 个基线版本、`integrity_check = ok`；`history.db` 144 个对话 / 545 条记录未改动。

### 回滚

1. 退出 ZERO（托盘 → 退出）；
2. 用 `memory.db.bak-v1` 覆盖 `memory.db`；删除 `governance.db`（只含策略、任务和审计）；
3. 安装 v1.2.0。

注意：v1.2.0 不认识新增列，如果不恢复备份直接用旧版本打开 v2 库，回收站和已归档的记忆会重新显示为普通记忆（未实测旧版本对 v2 库的全部行为，建议始终用备份回滚）。备份之后新增或修改的记忆在回滚后会丢失。

## 3. 权限

| 权限 | 工具 | 有限制 | 全电脑访问 | 依赖 |
|---|---|:---:|:---:|---|
| 登记任务计划 | `task_create` `task_update` `task_get` `task_list` `task_complete` | 开 | 开 | — |
| 处理记忆维护任务 | `governance_status` `governance_claim` `governance_heartbeat` `governance_submit` `governance_release` | 跟随「保存和修改记忆」 | 同左 | 保存和修改记忆、启用记忆库 |
| 查看窗口和控件 | `ui_list_windows` `ui_inspect` `ui_wait` `ui_compare` `ui_request_access` | 关 | 开 | — |
| 窗口截图和录制 | `ui_screenshot` `ui_record` | 关 | 开 | — |
| 操作窗口控件 | `ui_action` | 关 | **关** | 查看窗口和控件 + 逐窗口授权 |
| 打开和查看网页 | `browser_open` `browser_inspect` `browser_close` | 关 | 开 | — |
| 操作网页 | `browser_action` | 关 | **关** | 打开和查看网页 |

- 依赖未满足时界面会提示，服务端也不会提供该工具；服务端对每次调用再做一次权限检查（纵深防御）。
- `memory_versions` 归入「搜索记忆」。

## 4. MCP 工具

### 记忆（新增 / 变化）

| 工具 | 说明 |
|---|---|
| `memory_search(query, tags, limit, include_archived)` | 默认不返回回收站和已归档记忆；结果带版本号 |
| `memory_update(memory_id, content, tags, expected_revision)` | 传 `expected_revision` 时做乐观锁，冲突返回 `REVISION_CONFLICT` |
| `memory_delete(memory_id)` | 移到回收站（可在治理中心恢复），受保护 / 置顶的不能删除 |
| `memory_versions(memory_id, limit)` | 查看历史版本 |

### 任务

| 工具 | 说明 |
|---|---|
| `task_create(title, plan, kind, phase)` | 登记任务，返回 `task_id` 和 `task_handle`（后续更新凭证） |
| `task_update(task_id, task_handle, phase, plan, note)` | 更新进度 |
| `task_get` / `task_list(state, kind, mine, limit)` | 查看任务 |
| `task_complete(task_id, task_handle, result, evidence, failed)` | 完成或失败；返回服务端记录的任务期间工具调用次数（`activity_calls`），用于核对 |

任务模式（策略 `task.level`）：轻量（不提示）/ 标准（连续修改 6 次未登记时提示一次，默认）/ 严格（`task.strict_tools` 中的工具必须先登记任务，可再要求用户在任务中心批准）。

### 记忆维护

| 工具 | 说明 |
|---|---|
| `governance_status` | 待处理候选、任务、上次检查 |
| `governance_claim(task_id)` | 领取一个维护任务，返回候选内容、规则依据、`lease_token`（租约默认 15 分钟） |
| `governance_heartbeat(task_id, lease_token)` | 续约 |
| `governance_submit(task_id, lease_token, action, reason, confidence, evidence, …)` | 提交结论：`keep` `lower_priority` `merge` `compress` `correct` `archive` `delete_proposal` `needs_user_review`；同一结论重复提交返回 `DUPLICATE_SUBMIT` |
| `governance_release(task_id, lease_token, reason, defer)` | 放弃 / 推迟 |

同一任务同一时刻只会被一个执行者领取；租约过期自动回到队列，超过 `review.max_attempts` 次（默认 3）标记为失败，在任务中心「需要关注」中可重新排队。

### 视觉工作台（仅 Windows）

| 工具 | 说明 |
|---|---|
| `ui_list_windows(filter, include_untitled)` | 可见顶层窗口；返回 hwnd、PID、进程、标题、位置、是否已授权、桌面是否可用 |
| `ui_inspect(hwnd, element, max_depth, max_nodes)` | UIA 控件树；`path` 形如 `0.1.2`，坐标相对窗口；密码框只返回「已隐藏」 |
| `ui_wait(hwnd, element, state, timeout, title)` | 等待元素出现 / 消失 / 可用，或等待窗口标题 |
| `ui_screenshot(hwnd, max_width)` | PrintWindow 截取单个窗口（被遮挡也能截），返回图片和文件名；检测全黑 / 全白画面（`CAPTURE_BLANK`） |
| `ui_record(hwnd, seconds, fps, max_width)` | 录制帧序列（默认最长 15 秒），返回变化时间线和关键帧 |
| `ui_compare(image_a, image_b, tolerance)` | 比较两张截图，返回变化像素比例、变化区域和差异图 |
| `ui_request_access(hwnd, reason)` | 申请操作窗口；ZERO 托盘气泡提醒，用户在视觉工作台批准（限时 15 / 30 / 60 分钟） |
| `ui_action(hwnd, action, element, value, pid)` | `invoke` `set_value` `toggle` `select` `expand` `collapse` `focus` `scroll_into_view`（UIA 模式，不动鼠标）；`click` `double_click` `right_click` `scroll` `type` `keys`（真实输入，需要桌面可用）；`activate` `restore` `minimize` `maximize` |
| `browser_open(url)` | 用独立临时配置启动 Edge / Chrome（默认无头），只允许 `browser.allow` 中的主机（默认仅本机） |
| `browser_inspect(mode, selector, max_items)` | `summary`（可交互元素）/ `text` / `html` / `screenshot` / `console` |
| `browser_action(action, selector, text, timeout)` | `click` `fill` `type` `press` `select` `check` `scroll` `navigate` `back` `reload` `wait` `eval`；选择器支持 CSS 和 `text=文字` |
| `browser_close()` | 关闭浏览器并删除临时配置 |

安全约束：

- 授权绑定 **访问模式 + hwnd + PID**，每次操作都重新校验进程仍存活且 PID 未变（防窗口句柄复用）；
- 正在运行服务的 ZERO 主程序（`MCP_HOST_PID`）的所有窗口：不能查看、截图或操作；`consent.exe`、`LogonUI.exe`、`CredentialUIBroker.exe`、`LockApp.exe` 等安全窗口：不能操作；
- 密码框：不读取内容，`set_value` / `type` 返回 `SENSITIVE_FIELD`（网页 `input[type=password]` 同样）；
- `Win+L` 等系统组合键被拒绝；
- 紧急停止（视觉工作台左上角）立即拒绝所有 `ui_action` / `browser_action`，并写入审计；
- 每分钟操作 / 截图次数有上限（`ui.max_actions_per_min`、`ui.max_captures_per_min`）；同一窗口同一时刻只允许一个 AI 操作（`ui_locks`）；
- 所有操作（成功、失败、被拒绝）写入 `ui_actions` 审计，可在视觉工作台「操作日志」查看；输入内容只记录长度。

## 5. 错误码

所有新工具返回 JSON：成功 `{"ok": true, …}`；失败 `{"ok": false, "code": …, "message": …, "next_actions": [...]}`。

| 分类 | 错误码 |
|---|---|
| 通用 | `BAD_ARGUMENT` `INVALID_ARGUMENT` `NOT_FOUND` `PERMISSION_DENIED` `RATE_LIMITED` `TIMEOUT` `UNSUPPORTED` `GOVERNANCE_UNAVAILABLE` |
| 记忆 | `REVISION_CONFLICT` `PROTECTED` `DELETED` `NOT_IN_TRASH` `NOTHING_TO_CHANGE` |
| 任务 / 维护 | `TASK_REQUIRED` `APPROVAL_REQUIRED` `APPROVAL_REJECTED` `INVALID_STATE` `INVALID_KIND` `ALREADY_CLAIMED` `LEASE_INVALID` `LEASE_EXPIRED` `CANDIDATE_CLOSED` `INVALID_DECISION` `INVALID_ACTION` `USE_SUBMIT` `DUPLICATE_SUBMIT` `NO_TASK` |
| 窗口 | `DESKTOP_UNAVAILABLE` `WINDOW_NOT_FOUND` `WINDOW_CHANGED` `WINDOW_MINIMIZED` `WINDOW_EMPTY` `PROTECTED_WINDOW` `NOT_GRANTED` `REQUEST_PENDING` `ALREADY_GRANTED` `UI_LOCKED` `EMERGENCY_STOP` `NOT_FOREGROUND` |
| 控件 | `BAD_SELECTOR` `ELEMENT_NOT_FOUND` `ELEMENT_NOT_VISIBLE` `ELEMENT_DISABLED` `PATTERN_UNSUPPORTED` `READ_ONLY` `SENSITIVE_FIELD` `BAD_ACTION` `OPTION_NOT_FOUND` |
| 截图 | `CAPTURE_FAILED` `CAPTURE_BLANK` `PATH_DENIED` |
| 辅助进程 | `HELPER_UNAVAILABLE` `HELPER_FAILED` `HELPER_ERROR` `BAD_COMMAND` |
| 浏览器 | `BROWSER_NOT_FOUND` `BROWSER_FAILED` `BROWSER_ERROR` `BROWSER_BUSY` `BROWSER_CLOSED` `URL_BLOCKED` `SCRIPT_ERROR` |

## 6. 界面入口

- 主界面顶部新增「治理」（有待审批结论时显示数字角标）和「视觉」（有待批准的操作申请时显示角标，同时托盘气泡提醒）。
- **治理中心**：概览（统计卡片、上次 / 下次检查、当前策略）· 记忆治理（候选并排对比、规则依据、AI 结论批准 / 拒绝、直接处理：保留 / 降权 / 归档 / 删除到回收站 / 忽略 / 合并 / 更正）· 任务中心（筛选、详情、事件流、批准 / 拒绝 / 取消 / 重新排队）· 审核与回收站（恢复、彻底删除、保护、历史版本恢复、审计记录）· 策略设置（带校验的表单，保存后约 2 秒内生效，不需要重启服务）。
- **视觉工作台**：紧急停止、桌面可用状态、待批准申请横幅、窗口列表与筛选、窗口预览、控件树、限时授权 / 撤销、授权记录、操作日志。
- **记忆库窗口**：新增「保护」开关；删除改为「移到回收站」。
- 定时检查：ZERO 运行时按 `review.cycle_days`（默认 14 天）在后台检查，启动 2 分钟后才会做第一次；失败 1 天后重试。

## 7. 兼容性

- 旧客户端：原有工具名称和参数不变（`memory_update` 新增可选参数），旧客户端无需修改。
- 非 Windows（开发 / 测试）：窗口类工具自动不提供，浏览器工具仍可用（需要本机 Chrome / Chromium / Edge）。
- UIA 由 `ZERO.exe --uia-helper` 子进程提供（.NET UI Automation，MTA 线程），MCP 服务通过 `MCP_ZERO_EXE` 找到它；单独运行 `server.py` 而没有 ZERO.exe 时，控件树和控件操作不可用（列表和截图仍可用）。
- 浏览器通过 Chrome DevTools Protocol 直接控制（标准库 WebSocket），**没有使用 Playwright**，不需要额外下载浏览器。

## 8. 测试记录（2026-10-27）

| 测试 | 环境 | 结果 |
|---|---|---|
| 原有工具回归（文件、命令、历史、记忆、权限过滤） | Linux 沙箱，Python 3.13 + mcp | 通过 |
| 治理单元测试（迁移、扫描、候选、结论、租约、并发领取、恢复、彻底删除、策略校验） | 同上 | 通过 |
| 浏览器端到端（本机页面、填写 / 点击 / 文本、密码框拒绝、file:// 与外部地址拦截、页面内 fetch 外网被拦截、截图、控制台、审计） | Linux Chromium | 通过 |
| 真实数据迁移 | Windows 10，真实 `memory.db` / `history.db` 副本 | 通过（见第 2 节） |
| 窗口端到端（记事本列表 / 控件树 / 截图 / 未授权拒绝 / 申请 / 授权后输入与组合键 / PID 不符拒绝 / 等待 / 比较 / 录制 / 紧急停止；ZERO 主窗口受保护） | Windows 10，ZERO.exe 辅助进程 | 通过 |
| 浏览器端到端 | Windows 10，Edge 无头 | 通过 |
| 界面渲染（治理中心 5 个页签、视觉工作台、权限窗口） | Windows 10，`--snapshot-*` | 通过 |
| WPF 编译 | Windows 10 dotnet build / Linux 交叉编译 | 0 错误 0 警告 |

## 9. 已知限制

- **AI 审阅只在 AI 主动领取任务时发生**：ZERO 不会自己调用付费模型 API；没有 AI 连接时候选会一直等待，用户可以直接处理。
- **任务身份**：执行者按「访问模式 + 客户端名称（User-Agent）」识别；同一客户端的多个并发聊天会被视为同一个执行者（依靠 `task_handle` / `lease_token` 区分具体任务）。
- **浏览器允许列表**基于启动参数 `--proxy-server` + `--proxy-bypass-list` 实现：允许列表以外的请求全部发往一个不可用的本地代理，因此**会绕过系统代理设置**；允许列表中的主机直连。另外每次导航后再检查一次地址，不符合时回到 `about:blank` 并返回 `URL_BLOCKED`。
- 真实鼠标 / 键盘操作（`click` `type` `keys`）需要交互式桌面：锁屏、RDP 最小化或断开时返回 `DESKTOP_UNAVAILABLE`，UIA 模式的操作（`invoke` `set_value` 等）通常仍可用。
- 以管理员身份运行的窗口（且 ZERO 不是管理员）不能接收模拟输入（Windows UIPI 限制）。
- 部分程序的文本框不允许 UIA 直接写值（如 Windows 10 记事本返回 `READ_ONLY`），需要改用 `click` + `type`。
- 主界面整体截图（`--snapshot=`）需要独占单实例锁，本次测试时开发版 ZERO 正在运行，未截取新的主界面图；主界面新增按钮已收紧间距，但未在最小窗口宽度下目视确认。
