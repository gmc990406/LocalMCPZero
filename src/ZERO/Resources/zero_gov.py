"""
ZERO 治理核心：数据库迁移、记忆生命周期（版本 / 软删除 / 归档 / 恢复 / 审查决策）、治理任务队列（租约 / 幂等）、本地初审扫描。
- server.py（MCP 服务）直接 import 使用
- ZERO 的 WPF 界面通过命令行调用：python zero_gov.py --memory-db 路径 --gov-db 路径 <scan|apply|user-decide|restore|migrate> …
  （复杂的记忆变更只在这里实现一次，界面和 MCP 服务共用同一套规则）
所有写操作使用 BEGIN IMMEDIATE 事务；状态转换比对 revision / lease_token，避免多个 AI 重复处理。
"""
import contextlib, json, re, sqlite3, sys, time, uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCHEMA = json.loads((HERE / "zero_schema.json").read_text(encoding="utf-8"))
DEFAULTS = dict(SCHEMA["policy_defaults"])
ACTIONS = ("keep", "merge", "compress", "correct", "lower_priority", "archive", "delete_proposal", "needs_user_review")
ACTION_TEXT = {"keep": "保留", "merge": "合并", "compress": "压缩", "correct": "更正", "lower_priority": "降权",
               "archive": "归档", "delete_proposal": "建议删除（进回收站）", "needs_user_review": "交给用户判断"}
CATEGORY_TEXT = {"duplicate": "完全重复", "near_duplicate": "近似重复", "redundant": "内容被包含（冗余）", "too_long": "内容过长",
                 "inactive": "长期未被检索", "stale": "长期未更新", "expired": "已过期", "conflict": "可能矛盾"}
SELF_KINDS = ("manual_plan", "ui_validation")      # AI 自己登记、自己执行的任务
STATE_TEXT = {"draft": "草稿", "queued": "待领取", "leased": "已领取", "running": "进行中", "review_required": "待用户审核",
              "completed": "已完成", "deferred": "已搁置", "failed": "失败", "cancelled": "已取消"}


class GovError(Exception):
    """业务错误：带稳定错误码，MCP 工具会以 {"ok": false, "code": …} 的形式返回"""
    def __init__(self, code: str, message: str, next_actions=None, data=None):
        super().__init__(message)
        self.code, self.message, self.next_actions, self.data = code, message, list(next_actions or []), data

    def to_dict(self):
        d = {"ok": False, "code": self.code, "message": self.message}
        if self.next_actions:
            d["next_actions"] = self.next_actions
        if self.data is not None:
            d["data"] = self.data
        return d


def now() -> float:
    return time.time()


def connect(path: str) -> sqlite3.Connection:
    c = sqlite3.connect(path, timeout=10, isolation_level=None)
    c.row_factory = sqlite3.Row
    c.execute("PRAGMA busy_timeout=10000")
    return c


@contextlib.contextmanager
def tx(path: str):
    """写事务：BEGIN IMMEDIATE 先拿写锁，保证“读-判断-写”的原子性"""
    c = connect(path)
    try:
        c.execute("BEGIN IMMEDIATE")
        yield c
        c.execute("COMMIT")
    except BaseException:
        with contextlib.suppress(Exception):
            c.execute("ROLLBACK")
        raise
    finally:
        c.close()


@contextlib.contextmanager
def ro(path: str):
    c = connect(path)
    try:
        yield c
    finally:
        c.close()


# ======================= 迁移 =======================
def migrate(path: str, which: str, log=lambda m: None) -> int:
    """把数据库升级到 SCHEMA[which]["version"]。已有数据的库升级前先备份为 <库>.bak-v<旧版本>（每个旧版本只备份一次，不覆盖）。
    失败时回滚并抛出异常（调用方必须停用相关功能，不能把迁移失败当成空库继续使用）。"""
    spec = SCHEMA[which]
    target = int(spec["version"])
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    c = connect(path)
    try:
        c.execute("PRAGMA journal_mode=WAL")
        v = c.execute("PRAGMA user_version").fetchone()[0]
        if v >= target:
            return v
        has_data = c.execute("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").fetchone()[0] > 0
        if has_data:
            bak = Path(f"{path}.bak-v{v}")
            if not bak.exists():
                b = sqlite3.connect(str(bak))
                try:
                    c.backup(b)
                finally:
                    b.close()
                log(f"{p.name} 升级前已备份：{bak.name}")
        c.execute("BEGIN IMMEDIATE")
        try:
            v = c.execute("PRAGMA user_version").fetchone()[0]     # 拿到写锁后再确认一次（另一个进程可能刚升级完）
            if v < target:
                for sql in spec["base"]:
                    c.execute(sql)
                for step in spec["steps"]:
                    if int(step["v"]) > v:
                        for sql in step["sql"]:
                            try:
                                c.execute(sql)
                            except sqlite3.OperationalError as e:
                                if "duplicate column" not in str(e).lower():
                                    raise
                c.execute(f"PRAGMA user_version={target}")
            c.execute("COMMIT")
        except BaseException:
            with contextlib.suppress(Exception):
                c.execute("ROLLBACK")
            raise
        if v < target:
            log(f"{p.name} 已升级到 v{target}")
        return target
    finally:
        c.close()


# ======================= 策略设置（governance.db settings 表，界面修改后即时生效） =======================
_POLICY_CACHE: dict = {}


def get_policy(gov: str, ttl: float = 2.0) -> dict:
    hit = _POLICY_CACHE.get(gov)
    if hit and now() - hit[0] < ttl:
        return hit[1]
    pol = dict(DEFAULTS)
    if gov:
        try:
            with ro(gov) as c:
                for r in c.execute("SELECT key, value FROM settings"):
                    pol[r["key"]] = r["value"]
        except sqlite3.Error:
            pass
    _POLICY_CACHE[gov] = (now(), pol)
    return pol


def set_policy(gov: str, key: str, value: str):
    with tx(gov) as c:
        c.execute("INSERT INTO settings(key, value, updated) VALUES(?,?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated=excluded.updated",
                  (key, str(value), now()))
    _POLICY_CACHE.pop(gov, None)


def pint(pol: dict, key: str) -> int:
    try:
        return int(float(pol.get(key, DEFAULTS.get(key, "0"))))
    except (TypeError, ValueError):
        return int(float(DEFAULTS.get(key, "0") or 0))


def pfloat(pol: dict, key: str) -> float:
    try:
        return float(pol.get(key, DEFAULTS.get(key, "0")))
    except (TypeError, ValueError):
        return float(DEFAULTS.get(key, "0") or 0)


def plist(pol: dict, key: str) -> list:
    return [x.strip() for x in re.split(r"[,，;；\s]+", str(pol.get(key, "")).strip()) if x.strip()]


def pbool(pol: dict, key: str) -> bool:
    return str(pol.get(key, "0")).strip().lower() in ("1", "true", "yes", "on")


# ======================= 记忆生命周期 =======================
MEM_COLS = ("id, content, tags, scope, source, source_conv, created, updated, pinned, hits, last_used, revision, protected,"
            " archived_at, deleted_at, priority, quality_state, expires_at, merged_into")


def visible(scope: str, mode) -> bool:
    """mode=None 表示本机用户（看得到全部）"""
    return mode is None or scope in ("all", mode)


def is_protected(row, pol: dict) -> bool:
    """用户置顶、标记保护，或带有保护标签的记忆"""
    if row["pinned"] or row["protected"]:
        return True
    tags = {t.strip().lower() for t in (row["tags"] or "").split(",")}
    return any(t.lower() in tags for t in plist(pol, "review.protected_tags"))


def mem_get(c, mid: int, mode=None, allow_deleted=False):
    row = c.execute(f"SELECT {MEM_COLS} FROM memories WHERE id=?", (int(mid),)).fetchone()
    if not row or not visible(row["scope"], mode):
        raise GovError("NOT_FOUND", f"没有找到记忆 #{mid}")
    if row["deleted_at"] and not allow_deleted:
        raise GovError("DELETED", f"记忆 #{mid} 已在回收站中（用户可在 ZERO 的记忆治理中恢复）")
    return row


def _state(row) -> str:
    return "deleted" if row["deleted_at"] else "archived" if row["archived_at"] else "active"


def _version(c, mid: int, action: str, actor: dict, reason: str = ""):
    r = c.execute(f"SELECT {MEM_COLS} FROM memories WHERE id=?", (mid,)).fetchone()
    if r:
        c.execute("INSERT OR REPLACE INTO memory_versions(memory_id, revision, content, tags, scope, pinned, protected, state, changed_at,"
                  " actor_type, actor, action, reason) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",
                  (mid, r["revision"], r["content"], r["tags"], r["scope"], r["pinned"], r["protected"], _state(r), now(),
                   actor.get("type", "system"), actor.get("display", "")[:80], action, (reason or "")[:1000]))


def _event(c, mid: int, etype: str, actor: dict, before: int = 0, after: int = 0, status="ok", detail="", task_id=""):
    c.execute("INSERT INTO memory_events(memory_id, task_id, event_type, actor_type, actor_display, before_revision, after_revision,"
              " status, detail, created_at) VALUES(?,?,?,?,?,?,?,?,?,?)",
              (mid, task_id or "", etype, actor.get("type", "system"), actor.get("display", "")[:80], before, after, status,
               (detail or "")[:2000], now()))


def _bump(c, mid: int, sets: dict, expected: int = 0) -> int:
    """按 revision 乐观锁更新一条记忆，返回新 revision；revision 不一致时抛出 REVISION_CONFLICT"""
    row = c.execute("SELECT revision FROM memories WHERE id=?", (mid,)).fetchone()
    if not row:
        raise GovError("NOT_FOUND", f"没有找到记忆 #{mid}")
    cur = row[0]
    if expected and expected != cur:
        raise GovError("REVISION_CONFLICT", f"记忆 #{mid} 已被修改（当前版本 {cur}，你基于的版本 {expected}），未覆盖新内容",
                       ["重新用 memory_search 或 memory_versions 读取最新内容后再修改"], {"memory_id": mid, "current_revision": cur})
    cols = ", ".join(f"{k}=?" for k in sets)
    n = c.execute(f"UPDATE memories SET {cols}{', ' if cols else ''}revision=revision+1 WHERE id=? AND revision=?",
                  list(sets.values()) + [mid, cur]).rowcount
    if n != 1:
        raise GovError("REVISION_CONFLICT", f"记忆 #{mid} 正在被其他操作修改，请稍后重试")
    return cur + 1


def mem_add(path: str, mode: str, content: str, tags: str, source: str, conv: int, actor: dict, scope: str = "") -> tuple:
    """新增记忆（相同内容去重）。返回 (id, 是否新增)"""
    with tx(path) as c:
        sc = scope or mode
        row = c.execute("SELECT id FROM memories WHERE content=? AND deleted_at=0 AND scope IN ('all', ?)", (content, sc)).fetchone()
        if row:
            c.execute("UPDATE memories SET updated=?, tags=CASE WHEN ?='' THEN tags ELSE ? END WHERE id=?", (now(), tags, tags, row[0]))
            return row[0], False
        t = now()
        mid = c.execute("INSERT INTO memories(content, tags, scope, source, source_conv, created, updated) VALUES(?,?,?,?,?,?,?)",
                        (content, tags, sc, source[:60], conv, t, t)).lastrowid
        _version(c, mid, "create", actor)
        _event(c, mid, "create", actor, 0, 1)
        return mid, True


def mem_update(path: str, mode, mid: int, content=None, tags=None, actor=None, expected: int = 0, action="update", reason="",
               pol=None, task_id="") -> int:
    actor = actor or {"type": "user", "display": "用户"}
    pol = pol or DEFAULTS
    with tx(path) as c:
        row = mem_get(c, mid, mode)
        if actor.get("type") == "ai" and row["protected"] and content is not None and content != row["content"]:
            raise GovError("APPROVAL_REQUIRED", f"记忆 #{mid} 被用户标记为受保护，AI 不能直接修改内容",
                           ["如确需修改，请告诉用户在 ZERO 记忆库中修改，或在记忆维护任务中提交 correct 提案等待用户审批"])
        sets = {"updated": now()}
        if content is not None:
            sets["content"] = content
        if tags is not None:
            sets["tags"] = tags
        if len(sets) == 1:
            raise GovError("NOTHING_TO_CHANGE", "没有要修改的内容")
        rev = _bump(c, mid, sets, expected)
        _version(c, mid, action, actor, reason)
        _event(c, mid, action, actor, rev - 1, rev, detail=reason, task_id=task_id)
        return rev


def mem_set_flags(path: str, mid: int, actor: dict, **flags) -> int:
    """用户在界面上改置顶 / 保护 / 可见范围 / 优先级 / 失效时间（记录版本，可回溯）"""
    allowed = {"pinned", "protected", "scope", "priority", "expires_at"}
    sets = {k: v for k, v in flags.items() if k in allowed}
    with tx(path) as c:
        mem_get(c, mid, None, allow_deleted=True)
        rev = _bump(c, mid, sets)
        _version(c, mid, "flags", actor, json.dumps(sets, ensure_ascii=False))
        _event(c, mid, "flags", actor, rev - 1, rev, detail=json.dumps(sets, ensure_ascii=False))
        return rev


def mem_soft_delete(path: str, mode, mid: int, actor: dict, reason="", pol=None, task_id="", expected: int = 0) -> int:
    pol = pol or DEFAULTS
    with tx(path) as c:
        row = mem_get(c, mid, mode, allow_deleted=True)
        if row["deleted_at"]:
            return row["revision"]
        if actor.get("type") == "ai" and is_protected(row, pol):
            raise GovError("PROTECTED", f"记忆 #{mid} 已被用户置顶或保护，不能由 AI 删除", ["如确实需要删除，请让用户在 ZERO 记忆库中操作"])
        rev = _bump(c, mid, {"deleted_at": now()}, expected)
        _version(c, mid, "delete", actor, reason)
        _event(c, mid, "delete", actor, rev - 1, rev, detail=reason, task_id=task_id)
        return rev


def mem_restore(path: str, mid: int, actor: dict, revision: int = 0, reason="") -> int:
    """恢复：从回收站/归档中恢复；指定 revision 时把内容、标签恢复到该版本（作为新的版本保存，不删除任何历史）"""
    with tx(path) as c:
        row = mem_get(c, mid, None, allow_deleted=True)
        sets = {"deleted_at": 0, "archived_at": 0, "merged_into": 0, "updated": now()}
        if revision:
            v = c.execute("SELECT content, tags, scope FROM memory_versions WHERE memory_id=? AND revision=?", (mid, int(revision))).fetchone()
            if not v:
                raise GovError("NOT_FOUND", f"记忆 #{mid} 没有版本 {revision}")
            sets.update(content=v["content"], tags=v["tags"])
        rev = _bump(c, mid, sets)
        _version(c, mid, "restore", actor, reason or (f"恢复到版本 {revision}" if revision else "从回收站/归档恢复"))
        _event(c, mid, "restore", actor, rev - 1, rev, detail=f"revision={revision}" if revision else "")
        return rev


def mem_purge(path: str, mid: int, actor: dict) -> None:
    """永久删除（只允许用户对回收站中的记忆操作）：删除正文和所有版本，只保留一条不含正文的审计事件"""
    with tx(path) as c:
        row = mem_get(c, mid, None, allow_deleted=True)
        if not row["deleted_at"]:
            raise GovError("NOT_IN_TRASH", f"记忆 #{mid} 不在回收站中，请先删除到回收站")
        c.execute("DELETE FROM memories WHERE id=?", (mid,))
        c.execute("DELETE FROM memory_versions WHERE memory_id=?", (mid,))
        _event(c, mid, "purge", actor, row["revision"], 0, detail="永久删除（正文与版本已清除）")


def mem_versions(path: str, mode, mid: int, limit: int = 20) -> list:
    with ro(path) as c:
        mem_get(c, mid, mode, allow_deleted=True)
        return [dict(r) for r in c.execute("SELECT revision, content, tags, scope, pinned, protected, state, changed_at, actor_type, actor,"
                                           " action, reason FROM memory_versions WHERE memory_id=? ORDER BY revision DESC LIMIT ?",
                                           (mid, int(limit)))]


# ======================= 治理任务队列 =======================
TASK_COLS = ("id, kind, title, payload_json, state, priority, scope, created_at, updated_at, due_at, claimed_by, claimed_key, lease_token,"
             " lease_until, heartbeat_at, attempts, max_attempts, idempotency_key, related_conv_id, result_json, last_error, revision,"
             " created_by, level, approval, phase, plan_json, activity_calls")


def _tev(c, tid: str, event: str, actor: str = "", detail: str = ""):
    c.execute("INSERT INTO task_events(task_id, ts, event, actor, detail) VALUES(?,?,?,?,?)", (tid, now(), event, actor[:80], detail[:2000]))


def task_public(r, with_token=False) -> dict:
    d = {k: r[k] for k in r.keys() if k not in ("lease_token", "claimed_key", "payload_json", "result_json", "plan_json")}
    d["state_text"] = STATE_TEXT.get(r["state"], r["state"])
    for k in ("payload_json", "result_json", "plan_json"):
        try:
            d[k[:-5]] = json.loads(r[k] or "null")
        except ValueError:
            d[k[:-5]] = r[k]
    if with_token:
        d["lease_token"] = r["lease_token"]
    return d


def requeue_expired(c) -> int:
    """租约过期的任务：维护任务重新排队（超过重试上限进入失败/死信），AI 自己登记的计划任务标记为搁置"""
    t = now()
    n = 0
    for r in c.execute("SELECT id, kind, attempts, max_attempts FROM governance_tasks WHERE state IN ('leased','running') AND lease_until>0 AND lease_until<?",
                       (t,)).fetchall():
        if r["kind"] in SELF_KINDS:
            c.execute("UPDATE governance_tasks SET state='deferred', lease_token='', updated_at=?, revision=revision+1, last_error=? WHERE id=?",
                      (t, "LEASE_EXPIRED：执行者长时间没有活动", r["id"]))
            _tev(c, r["id"], "lease_expired", "system", "计划任务已搁置")
        elif r["attempts"] >= r["max_attempts"]:
            c.execute("UPDATE governance_tasks SET state='failed', lease_token='', updated_at=?, revision=revision+1, last_error=? WHERE id=?",
                      (t, "LEASE_EXPIRED：多次领取后都没有完成，已达重试上限", r["id"]))
            _tev(c, r["id"], "dead_letter", "system", "超过重试上限")
        else:
            c.execute("UPDATE governance_tasks SET state='queued', lease_token='', claimed_key='', lease_until=0, updated_at=?, revision=revision+1,"
                      " last_error=? WHERE id=?", (t, "LEASE_EXPIRED：上一次领取已超时，重新排队", r["id"]))
            _tev(c, r["id"], "requeued", "system", "租约过期")
        n += 1
    return n


def task_create(gov: str, kind: str, title: str, actor: dict, scope: str, payload=None, priority: int = 0, idem: str = "",
                level: str = "standard", plan=None, self_claim: bool = False, approval: str = "none", lease_minutes: int = 120,
                max_attempts: int = 3, related_conv: int = 0, c=None) -> tuple:
    """创建任务；idempotency_key 相同则返回已有任务。self_claim=True：由创建者直接执行（计划任务），返回 lease_token 作为 task_handle"""
    def go(c):
        if idem:
            r = c.execute(f"SELECT {TASK_COLS} FROM governance_tasks WHERE idempotency_key=?", (idem,)).fetchone()
            if r:
                return r, False
        tid = uuid.uuid4().hex[:12]
        t = now()
        token = uuid.uuid4().hex if self_claim else ""
        c.execute("INSERT INTO governance_tasks(id, kind, title, payload_json, state, priority, scope, created_at, updated_at, claimed_by, claimed_key,"
                  " lease_token, lease_until, heartbeat_at, attempts, max_attempts, idempotency_key, related_conv_id, created_by, level, approval, plan_json)"
                  " VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                  (tid, kind, title[:200], json.dumps(payload or {}, ensure_ascii=False), "running" if self_claim else "queued", int(priority), scope,
                   t, t, actor.get("display", "")[:80] if self_claim else "", actor.get("key", "") if self_claim else "", token,
                   t + lease_minutes * 60 if self_claim else 0, t if self_claim else 0, 1 if self_claim else 0, max_attempts, idem or None,
                   related_conv, actor.get("display", "")[:80], level, approval, json.dumps(plan or [], ensure_ascii=False)))
        _tev(c, tid, "created", actor.get("display", ""), title[:200])
        return c.execute(f"SELECT {TASK_COLS} FROM governance_tasks WHERE id=?", (tid,)).fetchone(), True
    if c is not None:
        return go(c)
    with tx(gov) as c2:
        return go(c2)


def task_get(gov: str, tid: str, mode=None, c=None):
    def go(c):
        r = c.execute(f"SELECT {TASK_COLS} FROM governance_tasks WHERE id=?", (str(tid),)).fetchone()
        if not r or not visible(r["scope"], mode):
            raise GovError("NOT_FOUND", f"没有找到任务 {tid}")
        return r
    if c is not None:
        return go(c)
    with ro(gov) as c2:
        return go(c2)


def task_list(gov: str, mode=None, state: str = "", kind: str = "", limit: int = 20, key: str = "") -> list:
    sql, ps = f"SELECT {TASK_COLS} FROM governance_tasks WHERE 1=1", []
    if mode is not None:
        sql += " AND scope IN ('all', ?)"; ps.append(mode)
    if state:
        sts = [s.strip() for s in state.split(",") if s.strip()]
        sql += f" AND state IN ({','.join('?' * len(sts))})"; ps += sts
    if kind:
        sql += " AND kind=?"; ps.append(kind)
    if key:
        sql += " AND claimed_key=?"; ps.append(key)
    sql += " ORDER BY CASE state WHEN 'running' THEN 0 WHEN 'leased' THEN 1 WHEN 'review_required' THEN 2 WHEN 'queued' THEN 3 ELSE 4 END,"
    sql += " priority DESC, updated_at DESC LIMIT ?"; ps.append(int(limit))
    with ro(gov) as c:
        return c.execute(sql, ps).fetchall()


def _lease(c, tid: str, token: str, mode):
    r = task_get(None, tid, mode, c=c)
    if not token or r["lease_token"] != token:
        raise GovError("LEASE_INVALID", f"任务 {tid} 的 lease_token 不正确或已被其他执行者领取", ["用 task_get / governance_status 查看任务当前状态"])
    if r["state"] not in ("leased", "running"):
        raise GovError("LEASE_INVALID", f"任务 {tid} 当前状态为 {STATE_TEXT.get(r['state'], r['state'])}，不能继续操作")
    if r["lease_until"] and r["lease_until"] < now():
        raise GovError("LEASE_EXPIRED", f"任务 {tid} 的租约已过期，任务会重新排队", ["重新调用 governance_claim 领取"])
    return r


def task_claim(gov: str, mode: str, actor: dict, kind: str = "memory_review", tid: str = "", lease_minutes: int = 15):
    """原子领取：同一任务同一时刻只会有一个执行者成功"""
    with tx(gov) as c:
        requeue_expired(c)
        for _ in range(5):
            if tid:
                r = c.execute("SELECT id, revision, state, approval FROM governance_tasks WHERE id=? AND scope IN ('all', ?)", (tid, mode)).fetchone()
                if not r:
                    raise GovError("NOT_FOUND", f"没有找到任务 {tid}")
                if r["state"] != "queued":
                    raise GovError("ALREADY_CLAIMED", f"任务 {tid} 当前状态为 {STATE_TEXT.get(r['state'], r['state'])}，不能领取")
            else:
                r = c.execute("SELECT id, revision FROM governance_tasks WHERE state='queued' AND kind=? AND scope IN ('all', ?) AND attempts<max_attempts"
                              " AND approval!='pending' ORDER BY priority DESC, created_at LIMIT 1", (kind, mode)).fetchone()
                if not r:
                    return None
            token, t = uuid.uuid4().hex, now()
            n = c.execute("UPDATE governance_tasks SET state='leased', lease_token=?, lease_until=?, heartbeat_at=?, claimed_by=?, claimed_key=?,"
                          " attempts=attempts+1, revision=revision+1, updated_at=? WHERE id=? AND revision=? AND state='queued'",
                          (token, t + lease_minutes * 60, t, actor.get("display", "")[:80], actor.get("key", ""), t, r["id"], r["revision"])).rowcount
            if n == 1:
                _tev(c, r["id"], "claimed", actor.get("display", ""))
                return c.execute(f"SELECT {TASK_COLS} FROM governance_tasks WHERE id=?", (r["id"],)).fetchone()
            if tid:
                raise GovError("ALREADY_CLAIMED", f"任务 {tid} 刚被其他执行者领取")
        return None


def task_heartbeat(gov: str, tid: str, token: str, mode: str, lease_minutes: int = 15):
    with tx(gov) as c:
        _lease(c, tid, token, mode)
        t = now()
        c.execute("UPDATE governance_tasks SET heartbeat_at=?, lease_until=?, updated_at=? WHERE id=?", (t, t + lease_minutes * 60, t, tid))
        return t + lease_minutes * 60


def task_release(gov: str, tid: str, token: str, mode: str, actor: dict, reason: str = "", defer: bool = False):
    with tx(gov) as c:
        _lease(c, tid, token, mode)
        st = "deferred" if defer else "queued"
        c.execute("UPDATE governance_tasks SET state=?, lease_token='', claimed_key='', lease_until=0, updated_at=?, revision=revision+1, last_error=?"
                  " WHERE id=?", (st, now(), reason[:500], tid))
        _tev(c, tid, "released", actor.get("display", ""), reason)


def task_update(gov: str, tid: str, token: str, mode: str, actor: dict, phase=None, plan=None, note: str = "", lease_minutes: int = 120):
    with tx(gov) as c:
        _lease(c, tid, token, mode)
        t = now()
        sets, ps = ["heartbeat_at=?", "lease_until=?", "updated_at=?", "revision=revision+1"], [t, t + lease_minutes * 60, t]
        if phase is not None:
            sets.append("phase=?"); ps.append(str(phase)[:100])
        if plan is not None:
            sets.append("plan_json=?"); ps.append(json.dumps(plan, ensure_ascii=False)[:8000])
        c.execute(f"UPDATE governance_tasks SET {', '.join(sets)} WHERE id=?", ps + [tid])
        if note or phase is not None:
            _tev(c, tid, "updated", actor.get("display", ""), (f"阶段：{phase}；" if phase else "") + note)
        return c.execute(f"SELECT {TASK_COLS} FROM governance_tasks WHERE id=?", (tid,)).fetchone()


def task_complete(gov: str, tid: str, token: str, mode: str, actor: dict, result: str = "", evidence: str = "", failed: bool = False):
    with tx(gov) as c:
        r = _lease(c, tid, token, mode)
        if r["kind"] == "memory_review":
            raise GovError("USE_SUBMIT", "记忆维护任务请用 governance_submit 提交结构化结论")
        res = {"result": result[:4000], "evidence": evidence[:4000], "activity_calls": r["activity_calls"],
               "verified": r["activity_calls"] > 0, "note": "activity_calls 是 ZERO 服务端记录到的、任务进行期间该执行者的工具调用次数"}
        st = "failed" if failed else "completed"
        c.execute("UPDATE governance_tasks SET state=?, lease_token='', lease_until=0, result_json=?, updated_at=?, revision=revision+1 WHERE id=?",
                  (st, json.dumps(res, ensure_ascii=False), now(), tid))
        _tev(c, tid, st, actor.get("display", ""), result[:500])
        return res


def active_task(gov: str, key: str):
    """调用方当前进行中的计划任务（用于严格模式门禁和活动计数）"""
    with ro(gov) as c:
        return c.execute(f"SELECT {TASK_COLS} FROM governance_tasks WHERE kind IN ('manual_plan','ui_validation') AND state='running' AND claimed_key=? AND lease_until>?"
                         " ORDER BY updated_at DESC LIMIT 1", (key, now())).fetchone()


def task_touch(gov: str, tid: str, lease_minutes: int = 120):
    with tx(gov) as c:
        t = now()
        c.execute("UPDATE governance_tasks SET activity_calls=activity_calls+1, heartbeat_at=?, lease_until=MAX(lease_until, ?) WHERE id=? AND state='running'",
                  (t, t + lease_minutes * 60, tid))


def task_user_action(gov: str, tid: str, action: str, actor: dict, reason: str = ""):
    """本机用户在 ZERO 界面中的操作：cancel / requeue / approve / reject / defer"""
    with tx(gov) as c:
        r = task_get(None, tid, None, c=c)
        t = now()
        if action == "cancel":
            if r["state"] in ("completed", "cancelled"):
                raise GovError("INVALID_STATE", "任务已结束")
            c.execute("UPDATE governance_tasks SET state='cancelled', lease_token='', lease_until=0, updated_at=?, revision=revision+1 WHERE id=?", (t, tid))
        elif action == "requeue":
            if r["state"] not in ("failed", "cancelled", "deferred", "review_required"):
                raise GovError("INVALID_STATE", "只有失败、取消、搁置或待审核的任务可以重新排队")
            nxt = "queued"
            c.execute("UPDATE governance_tasks SET state=?, attempts=0, lease_token='', claimed_key='', lease_until=0, last_error='', updated_at=?,"
                      " revision=revision+1 WHERE id=?", (nxt, t, tid))
        elif action in ("approve", "reject"):
            c.execute("UPDATE governance_tasks SET approval=?, updated_at=?, revision=revision+1 WHERE id=?",
                      ("approved" if action == "approve" else "rejected", t, tid))
            if action == "reject" and r["state"] in ("running", "leased", "queued"):
                c.execute("UPDATE governance_tasks SET state='cancelled', lease_token='', lease_until=0 WHERE id=?", (tid,))
        else:
            raise GovError("INVALID_ACTION", f"未知操作 {action}")
        _tev(c, tid, "user_" + action, actor.get("display", "用户"), reason)


# ======================= 本地初审（确定性规则，不调用模型） =======================
_NORM_RX = re.compile(r"[\s\u3000，。、；：！？,.;:!?\"'“”‘’()（）\[\]【】<>《》\-_/\\|·`~*#]+")
_DATE_RX = re.compile(r"(?:到期|截止|有效期至|有效期到|过期|失效|until|expires?|valid until)\D{0,8}(\d{4})[-/.年](\d{1,2})[-/.月](\d{1,2})", re.I)
_VALUE_RX = re.compile(r"[A-Za-z]:\\[^\s，。,;；]+|/(?:[\w.-]+/)+[\w.-]*|\bv?\d+(?:\.\d+){1,3}\b|\b\d{2,}\b|https?://\S+")


def _norm(s: str) -> str:
    return _NORM_RX.sub("", (s or "").lower())


def _grams(s: str) -> set:
    n = _norm(s)
    return {n[i:i + 2] for i in range(len(n) - 1)} if len(n) > 1 else ({n} if n else set())


def _scope_of(scopes) -> str:
    s = set(scopes) - {"all"}
    if not s:
        return "all"
    return s.pop() if len(s) == 1 else "mixed"


def _risk(cat: str, rows, pol) -> str:
    if any(is_protected(r, pol) for r in rows) or cat == "conflict":
        return "high"
    if cat in ("duplicate", "inactive", "stale", "expired"):
        return "low"
    return "medium"


def scan(mem: str, gov: str, trigger: str = "manual", log=lambda m: None) -> dict:
    """扫描记忆库，产生审查候选并为每个新候选创建维护任务。只产生候选和依据，不修改任何记忆内容。"""
    pol = get_policy(gov, ttl=0)
    t0 = now()
    stats = {"memories": 0, "new_candidates": 0, "reopened": 0, "skipped_existing": 0, "stale_candidates": 0, "tasks_created": 0,
             "requeued_leases": 0, "by_category": {}}
    with tx(gov) as g:
        run_id = g.execute("INSERT INTO scan_runs(started, trigger) VALUES(?,?)", (t0, trigger)).lastrowid
        stats["requeued_leases"] = requeue_expired(g)
    try:
        limit = pint(pol, "review.scan_max_items")
        with ro(mem) as c:
            rows = c.execute(f"SELECT {MEM_COLS} FROM memories WHERE deleted_at=0 AND merged_into=0 ORDER BY id LIMIT ?", (limit,)).fetchall()
        stats["memories"] = len(rows)
        found = []          # (category, [ids], signals)
        t = now()
        day = 86400
        sim_th = max(0.5, min(0.99, pfloat(pol, "review.similarity")))
        grams = {r["id"]: _grams(r["content"]) for r in rows}
        norms = {r["id"]: _norm(r["content"]) for r in rows}
        byid = {r["id"]: r for r in rows}
        active = [r for r in rows if not r["archived_at"]]
        # 1. 完全重复（规范化后文本相同）
        groups = {}
        for r in active:
            if norms[r["id"]]:
                groups.setdefault(norms[r["id"]], []).append(r["id"])
        dup_ids = set()
        for ids in groups.values():
            if len(ids) > 1:
                found.append(("duplicate", ids, {"rule": "规范化后（忽略空白、标点、大小写）文本完全相同", "count": len(ids)}))
                dup_ids.update(ids)
        # 2. 近似重复 / 被包含（倒排索引只比较有共同片段的记忆）
        index = {}
        for r in active:
            for gm in grams[r["id"]]:
                index.setdefault(gm, []).append(r["id"])
        pairs = {}
        for gm, ids in index.items():
            if len(ids) > 200:      # 太常见的片段不参与配对
                continue
            for i in range(len(ids)):
                for j in range(i + 1, len(ids)):
                    k = (ids[i], ids[j]) if ids[i] < ids[j] else (ids[j], ids[i])
                    pairs[k] = pairs.get(k, 0) + 1
        for (a, b), inter in pairs.items():
            if a in dup_ids and b in dup_ids and norms[a] == norms[b]:
                continue
            ga, gb = grams[a], grams[b]
            if not ga or not gb:
                continue
            jac = inter / len(ga | gb)
            cont = inter / min(len(ga), len(gb))
            va = set(_VALUE_RX.findall(byid[a]["content"]))
            vb = set(_VALUE_RX.findall(byid[b]["content"]))
            if jac >= 0.45 and va and vb and va != vb and not (va <= vb or vb <= va):
                found.append(("conflict", [a, b], {"rule": f"内容相似（{jac:.2f}）但其中的路径/版本/数值不同，可能互相矛盾（规则推断，需要复审）",
                                                   "values_a": sorted(va - vb)[:5], "values_b": sorted(vb - va)[:5]}))
            elif jac >= sim_th:
                found.append(("near_duplicate", [a, b], {"rule": f"字符片段相似度 {jac:.2f} ≥ 阈值 {sim_th:.2f}", "similarity": round(jac, 3)}))
            elif cont >= 0.9 and min(len(ga), len(gb)) >= 6:
                small = a if len(ga) <= len(gb) else b
                found.append(("redundant", [a, b], {"rule": f"#{small} 的内容有 {cont:.0%} 包含在另一条中", "containment": round(cont, 3)}))
        # 3. 单条记忆的规则
        max_len = pint(pol, "review.max_len")
        inact, stale = pint(pol, "review.inactive_days"), pint(pol, "review.stale_days")
        for r in active:
            mid, prot = r["id"], is_protected(r, pol)
            if max_len and len(r["content"]) > max_len:
                found.append(("too_long", [mid], {"rule": f"内容 {len(r['content'])} 字，超过 {max_len} 字", "length": len(r["content"])}))
            exp = r["expires_at"]
            if not exp:
                m = _DATE_RX.search(r["content"])
                if m:
                    with contextlib.suppress(ValueError, OverflowError):
                        exp = time.mktime((int(m.group(1)), int(m.group(2)), int(m.group(3)), 23, 59, 59, 0, 0, -1))
            if exp and exp < t:
                found.append(("expired", [mid], {"rule": "记忆中写明的失效日期已过" if not r["expires_at"] else "已超过设置的失效时间",
                                                 "expires": time.strftime("%Y-%m-%d", time.localtime(exp))}))
            if prot:
                continue            # 置顶/保护的记忆不参与“低活跃 / 久未更新”判断
            ref = r["last_used"] or r["created"]
            is_inactive = inact and t - ref > inact * day and t - r["created"] > inact * day
            is_stale = stale and t - r["updated"] > stale * day
            upd = time.strftime("%Y-%m-%d", time.localtime(r["updated"]))
            if is_inactive:
                found.append(("inactive", [mid], {"rule": f"超过 {inact} 天没有被检索返回（只表示没被搜到，不代表没有价值）"
                                                  + (f"；并且超过 {stale} 天没有更新" if is_stale else ""),
                                                  "last_returned": time.strftime("%Y-%m-%d", time.localtime(r["last_used"])) if r["last_used"] else "从未",
                                                  "hits": r["hits"], "updated": upd}))
            elif is_stale:
                found.append(("stale", [mid], {"rule": f"超过 {stale} 天没有更新，内容可能已过时", "updated": upd}))
        # 4. 写入候选（去重、复查间隔、批量上限），再为新候选创建维护任务
        batch = pint(pol, "review.batch_limit")
        recheck = pint(pol, "review.recheck_days") * day
        created = []
        with tx(mem) as c:
            # 已失效的候选：涉及的记忆被删除或内容已变化
            for cr in c.execute("SELECT candidate_id, memory_ids_json, revisions_json, task_id FROM memory_review_candidates WHERE status='open'").fetchall():
                ids = json.loads(cr["memory_ids_json"])
                revs = json.loads(cr["revisions_json"] or "{}")
                cur = {str(r["id"]): r["revision"] for r in rows if r["id"] in ids}
                if len(cur) != len(ids) or any(str(i) in revs and revs[str(i)] != cur.get(str(i)) for i in ids):
                    c.execute("UPDATE memory_review_candidates SET status='stale', updated_at=? WHERE candidate_id=?", (t, cr["candidate_id"]))
                    stats["stale_candidates"] += 1
                    if cr["task_id"]:
                        created.append(("cancel", cr["task_id"], None, None))
            for cat, ids, sig in found:
                if len(created) - sum(1 for x in created if x[0] == "cancel") >= batch:
                    break
                ids = sorted(set(ids))
                key = f"{cat}:{','.join(map(str, ids))}"
                rs = [byid[i] for i in ids]
                scope = _scope_of(r["scope"] for r in rs)
                risk = _risk(cat, rs, pol)
                revs = json.dumps({str(r["id"]): r["revision"] for r in rs})
                ex = c.execute("SELECT candidate_id, status, next_check_at FROM memory_review_candidates WHERE dedupe_key=?", (key,)).fetchone()
                if ex:
                    if ex["status"] in ("open", "review_required") or ex["next_check_at"] > t:
                        stats["skipped_existing"] += 1
                        continue
                    c.execute("UPDATE memory_review_candidates SET status='open', signals_json=?, risk_level=?, scope=?, revisions_json=?, updated_at=?,"
                              " next_check_at=? WHERE candidate_id=?", (json.dumps(sig, ensure_ascii=False), risk, scope, revs, t, t + recheck, ex["candidate_id"]))
                    cid = ex["candidate_id"]
                    stats["reopened"] += 1
                else:
                    cid = c.execute("INSERT INTO memory_review_candidates(memory_ids_json, category, signals_json, risk_level, created_at, updated_at,"
                                    " next_check_at, status, scope, dedupe_key, revisions_json) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                                    (json.dumps(ids), cat, json.dumps(sig, ensure_ascii=False), risk, t, t, t + recheck, "open", scope, key, revs)).lastrowid
                    stats["new_candidates"] += 1
                stats["by_category"][cat] = stats["by_category"].get(cat, 0) + 1
                created.append(("new", cid, cat, (ids, scope, risk, int(t))))
        with tx(gov) as g:
            maxa = pint(pol, "review.max_attempts")
            sysactor = {"type": "system", "display": "ZERO 初审"}
            for kind, cid, cat, extra in created:
                if kind == "cancel":
                    g.execute("UPDATE governance_tasks SET state='cancelled', updated_at=?, revision=revision+1, last_error='候选已失效（记忆已变化）'"
                              " WHERE id=? AND state IN ('queued','deferred')", (t, cid))
                    continue
                ids, scope, risk, stamp = extra
                title = f"记忆维护：{CATEGORY_TEXT.get(cat, cat)} " + "、".join(f"#{i}" for i in ids[:5])
                r, new = task_create(gov, "memory_review", title, sysactor, scope, {"candidate_id": cid, "category": cat, "memory_ids": ids, "risk": risk},
                                     priority={"high": 2, "medium": 1}.get(risk, 0), idem=f"cand:{cid}:{stamp}", max_attempts=maxa, c=g)
                if scope == "mixed":    # 跨访问模式的候选：AI 不能领取，只能由用户在 ZERO 中处理
                    g.execute("UPDATE governance_tasks SET state='review_required', last_error='涉及不同访问模式的记忆，只能由用户处理' WHERE id=?", (r["id"],))
                if new:
                    stats["tasks_created"] += 1
                with tx(mem) as c:
                    c.execute("UPDATE memory_review_candidates SET task_id=? WHERE candidate_id=?", (r["id"], cid))
            g.execute("UPDATE scan_runs SET finished=?, ok=1, stats_json=? WHERE id=?", (now(), json.dumps(stats, ensure_ascii=False), run_id))
            g.execute("INSERT INTO settings(key, value, updated) VALUES('review.last_scan', ?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value,"
                      " updated=excluded.updated", (str(now()), now()))
        _POLICY_CACHE.pop(gov, None)
        log(f"记忆初审完成：{json.dumps(stats, ensure_ascii=False)}")
        return stats
    except BaseException as e:
        with contextlib.suppress(Exception):
            with tx(gov) as g:
                g.execute("UPDATE scan_runs SET finished=?, ok=0, error=? WHERE id=?", (now(), str(e)[:1000], run_id))
        raise


# ======================= 复审：领取内容、校验结论、自动/人工提交 =======================
def candidate_view(mem: str, cid: int, mode=None) -> dict:
    with ro(mem) as c:
        cr = c.execute("SELECT * FROM memory_review_candidates WHERE candidate_id=?", (int(cid),)).fetchone()
        if not cr or (mode is not None and cr["scope"] not in ("all", mode)):
            raise GovError("NOT_FOUND", f"没有找到候选 {cid}")
        ids = json.loads(cr["memory_ids_json"])
        mems = []
        for i in ids:
            r = c.execute(f"SELECT {MEM_COLS} FROM memories WHERE id=?", (i,)).fetchone()
            if r and visible(r["scope"], mode):
                mems.append({"id": r["id"], "revision": r["revision"], "content": r["content"], "tags": r["tags"], "scope": r["scope"],
                             "pinned": bool(r["pinned"]), "protected": bool(r["protected"]), "archived": bool(r["archived_at"]),
                             "deleted": bool(r["deleted_at"]), "priority": r["priority"], "source": r["source"],
                             "created": time.strftime("%Y-%m-%d", time.localtime(r["created"])),
                             "updated": time.strftime("%Y-%m-%d", time.localtime(r["updated"])),
                             "last_returned": time.strftime("%Y-%m-%d", time.localtime(r["last_used"])) if r["last_used"] else "从未",
                             "times_returned": r["hits"]})
        return {"candidate_id": cr["candidate_id"], "category": cr["category"], "category_text": CATEGORY_TEXT.get(cr["category"], cr["category"]),
                "signals": json.loads(cr["signals_json"] or "{}"), "risk_level": cr["risk_level"], "scope": cr["scope"], "status": cr["status"],
                "memories": mems}


def claim_review(mem: str, gov: str, mode: str, actor: dict, tid: str = "", lease_minutes: int = 15):
    """领取一个记忆维护任务并返回候选内容；候选已失效的任务自动取消并继续领取下一个"""
    for _ in range(10):
        r = task_claim(gov, mode, actor, "memory_review", tid, lease_minutes)
        if not r:
            return None, None
        payload = json.loads(r["payload_json"] or "{}")
        try:
            view = candidate_view(mem, int(payload.get("candidate_id") or 0), mode)
        except GovError:
            view = None
        if view and view["status"] == "open" and len(view["memories"]) == len(payload.get("memory_ids") or view["memories"]):
            payload["claimed_revisions"] = {str(m["id"]): m["revision"] for m in view["memories"]}
            with tx(gov) as g:
                g.execute("UPDATE governance_tasks SET payload_json=? WHERE id=?", (json.dumps(payload, ensure_ascii=False), r["id"]))
                r = g.execute(f"SELECT {TASK_COLS} FROM governance_tasks WHERE id=?", (r["id"],)).fetchone()
            return r, view
        with tx(gov) as g:
            g.execute("UPDATE governance_tasks SET state='cancelled', lease_token='', lease_until=0, updated_at=?, revision=revision+1,"
                      " last_error='候选已失效（记忆已变化或已处理）' WHERE id=?", (now(), r["id"]))
        if tid:
            raise GovError("CANDIDATE_CLOSED", f"任务 {tid} 对应的候选已失效，已自动取消")
    return None, None


def validate_decision(d: dict, ids: list) -> dict:
    a = str(d.get("action", "")).strip()
    if a not in ACTIONS:
        raise GovError("INVALID_DECISION", f"action 必须是 {', '.join(ACTIONS)} 之一")
    reason = str(d.get("reason", "")).strip()
    if len(reason) < 4:
        raise GovError("INVALID_DECISION", "reason 必填：请说明判断理由")
    try:
        conf = float(d.get("confidence", 0.5))
    except (TypeError, ValueError):
        raise GovError("INVALID_DECISION", "confidence 必须是 0~1 的数字")
    if not 0 <= conf <= 1:
        raise GovError("INVALID_DECISION", "confidence 必须是 0~1 的数字")
    ev = d.get("evidence", "")
    ev = "\n".join(map(str, ev)) if isinstance(ev, (list, tuple)) else str(ev or "")
    out = {"action": a, "reason": reason[:2000], "confidence": conf, "evidence": ev[:4000], "impact": str(d.get("impact", "") or "")[:1000]}
    tgt = int(d.get("target_id") or 0)
    if a in ("merge", "compress", "correct"):
        nc = str(d.get("new_content", "") or "").strip()
        if not nc:
            raise GovError("INVALID_DECISION", f"{a} 需要提供 new_content（合并/压缩/更正后的完整内容）")
        out["new_content"] = nc[:2000]
        if d.get("new_tags"):
            out["new_tags"] = str(d["new_tags"])[:300]
        if a == "merge" and len(ids) < 2:
            raise GovError("INVALID_DECISION", "merge 至少需要两条记忆")
        if not tgt:
            tgt = ids[0]
    if tgt and tgt not in ids:
        raise GovError("INVALID_DECISION", f"target_id 必须是候选中的记忆之一：{ids}")
    out["target_id"] = tgt
    return out


def _decision_risk(action: str, mems: list, cand_risk: str, pol) -> str:
    if action in ("delete_proposal", "needs_user_review") or any(m["pinned"] or m["protected"] for m in mems):
        return "high"
    if action in ("merge", "compress", "correct", "archive"):
        return "medium" if cand_risk != "high" else "high"
    return "low"


def submit_decision(mem: str, gov: str, mode: str, actor: dict, tid: str, token: str, d: dict) -> dict:
    """AI 提交复审结论。结论是“提案”：低风险且在自动提交白名单内的直接应用，其余等待用户在 ZERO 中审批"""
    pol = get_policy(gov)
    idem = f"task:{tid}:{token}"
    with ro(mem) as c:
        ex = c.execute("SELECT decision_id, status FROM memory_review_decisions WHERE idem_key=?", (idem,)).fetchone()
    if ex:
        return {"ok": True, "code": "DUPLICATE_SUBMIT", "decision_id": ex["decision_id"], "status": ex["status"], "message": "这个结论已经提交过，不会重复处理"}
    with ro(gov) as g:
        tr = _lease(g, tid, token, mode)
    payload = json.loads(tr["payload_json"] or "{}")
    cid = int(payload.get("candidate_id") or 0)
    view = candidate_view(mem, cid, mode)
    if view["status"] not in ("open",):
        raise GovError("CANDIDATE_CLOSED", f"候选 {cid} 已经处理过（{view['status']}）")
    ids = [m["id"] for m in view["memories"]]
    dec = validate_decision(d, ids)
    expected = {str(m["id"]): m["revision"] for m in view["memories"]}
    claimed = payload.get("claimed_revisions") or {}
    if claimed and any(claimed.get(k) not in (None, v) for k, v in expected.items()):
        raise GovError("REVISION_CONFLICT", "领取任务后记忆已被修改，请释放任务后重新领取", ["governance_release 后再 governance_claim"])
    risk = _decision_risk(dec["action"], view["memories"], view["risk_level"], pol)
    auto = dec["action"] in plist(pol, "review.auto_apply") and risk == "low" and view["scope"] != "mixed"
    with tx(mem) as c:
        ex = c.execute("SELECT decision_id, status FROM memory_review_decisions WHERE idem_key=?", (idem,)).fetchone()
        if ex:
            return {"ok": True, "code": "DUPLICATE_SUBMIT", "decision_id": ex["decision_id"], "status": ex["status"], "message": "这个结论已经提交过"}
        did = c.execute("INSERT INTO memory_review_decisions(candidate_id, task_id, action, payload_json, reason, confidence, evidence, impact, actor_type,"
                        " actor_display, status, risk_level, expected_json, idem_key, scope, created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                        (cid, tid, dec["action"], json.dumps(dec, ensure_ascii=False), dec["reason"], dec["confidence"], dec["evidence"], dec["impact"],
                         "ai", actor.get("display", "")[:80], "proposed", risk, json.dumps(expected), idem, view["scope"], now())).lastrowid
        if not auto:
            c.execute("UPDATE memory_review_candidates SET status='review_required', updated_at=? WHERE candidate_id=?", (now(), cid))
    if auto:
        res = apply_decision(mem, gov, did, {"type": "system", "display": "自动提交（低风险）"})
        return {"ok": res.get("ok", False), "decision_id": did, "status": res.get("status"), "auto_applied": True, "message": res.get("message", "")}
    with tx(gov) as g:
        g.execute("UPDATE governance_tasks SET state='review_required', lease_token='', lease_until=0, updated_at=?, revision=revision+1, result_json=? WHERE id=?",
                  (now(), json.dumps({"decision_id": did, "action": dec["action"], "risk": risk}, ensure_ascii=False), tid))
        _tev(g, tid, "proposed", actor.get("display", ""), f"{ACTION_TEXT[dec['action']]}：{dec['reason'][:300]}")
    return {"ok": True, "decision_id": did, "status": "proposed", "auto_applied": False, "risk_level": risk,
            "message": f"结论已保存为提案（{ACTION_TEXT[dec['action']]}，风险 {risk}），需要用户在 ZERO「治理中心」中审批后才会生效"}


def apply_decision(mem: str, gov: str, did: int, actor: dict, override: dict = None) -> dict:
    """应用一条结论（自动提交或用户批准）。提交时比对 expected revision，内容已变化则标记冲突，不覆盖。"""
    pol = get_policy(gov)
    with ro(mem) as c:
        dr = c.execute("SELECT * FROM memory_review_decisions WHERE decision_id=?", (int(did),)).fetchone()
    if not dr:
        raise GovError("NOT_FOUND", f"没有找到结论 {did}")
    if dr["status"] not in ("proposed", "conflict", "failed"):
        return {"ok": True, "status": dr["status"], "message": f"结论 {did} 已是 {dr['status']} 状态"}
    dec = json.loads(dr["payload_json"] or "{}")
    if override:
        dec.update({k: v for k, v in override.items() if v not in (None, "")})
    expected = {int(k): v for k, v in json.loads(dr["expected_json"] or "{}").items()}
    a, tgt, t = dec["action"], int(dec.get("target_id") or 0), now()
    status, msg, err = "applied", "", ""
    try:
        with tx(mem) as c:
            rows = {}
            for mid, rev in expected.items():
                r = c.execute(f"SELECT {MEM_COLS} FROM memories WHERE id=?", (mid,)).fetchone()
                if not r or r["deleted_at"]:
                    raise GovError("REVISION_CONFLICT", f"记忆 #{mid} 已被删除")
                if r["revision"] != rev:
                    raise GovError("REVISION_CONFLICT", f"记忆 #{mid} 在提出结论后被修改过（版本 {rev} → {r['revision']}）")
                rows[mid] = r
            tag = f"review:{did}"
            if a == "keep":
                for mid in rows:
                    c.execute("UPDATE memories SET quality_state='reviewed_keep' WHERE id=?", (mid,))
                    _event(c, mid, "review_keep", actor, rows[mid]["revision"], rows[mid]["revision"], detail=dec.get("reason", ""), task_id=dr["task_id"])
                msg = "已确认保留"
            elif a in ("compress", "correct"):
                mid = tgt or next(iter(rows))
                sets = {"content": dec["new_content"], "updated": t}
                if dec.get("new_tags"):
                    sets["tags"] = dec["new_tags"]
                rev = _bump(c, mid, sets, rows[mid]["revision"])
                _version(c, mid, a, actor, f"{tag} {dec.get('reason', '')}")
                _event(c, mid, a, actor, rev - 1, rev, detail=dec.get("reason", ""), task_id=dr["task_id"])
                msg = f"已{ACTION_TEXT[a]}记忆 #{mid}"
            elif a == "merge":
                sets = {"content": dec["new_content"], "updated": t}
                if dec.get("new_tags"):
                    sets["tags"] = dec["new_tags"]
                else:
                    allt = []
                    for r in rows.values():
                        allt += [x.strip() for x in r["tags"].split(",") if x.strip()]
                    sets["tags"] = ", ".join(dict.fromkeys(allt))[:300]
                if any(r["pinned"] for r in rows.values()):
                    sets["pinned"] = 1
                rev = _bump(c, tgt, sets, rows[tgt]["revision"])
                _version(c, tgt, "merge", actor, f"{tag} 合并 {', '.join('#%d' % m for m in rows if m != tgt)}")
                _event(c, tgt, "merge_target", actor, rev - 1, rev, detail=dec.get("reason", ""), task_id=dr["task_id"])
                for mid, r in rows.items():
                    if mid == tgt:
                        continue
                    rv = _bump(c, mid, {"archived_at": t, "merged_into": tgt}, r["revision"])
                    _version(c, mid, "merged_into", actor, f"{tag} 合并到 #{tgt}")
                    _event(c, mid, "merged_into", actor, rv - 1, rv, detail=f"合并到 #{tgt}", task_id=dr["task_id"])
                    c.execute("INSERT OR IGNORE INTO memory_relations(from_id, to_id, kind, created_at, source) VALUES(?,?,?,?,?)",
                              (mid, tgt, "merged_into", t, tag))
                msg = f"已合并到记忆 #{tgt}，其余已归档（可恢复）"
            elif a == "lower_priority":
                for mid, r in rows.items():
                    rv = _bump(c, mid, {"priority": max(-3, r["priority"] - 1)}, r["revision"])
                    _version(c, mid, "lower_priority", actor, tag)
                    _event(c, mid, "lower_priority", actor, rv - 1, rv, detail=dec.get("reason", ""), task_id=dr["task_id"])
                msg = "已降低排序优先级（内容不变）"
            elif a == "archive":
                for mid, r in rows.items():
                    if tgt and mid != tgt:
                        continue
                    rv = _bump(c, mid, {"archived_at": t}, r["revision"])
                    _version(c, mid, "archive", actor, tag)
                    _event(c, mid, "archive", actor, rv - 1, rv, detail=dec.get("reason", ""), task_id=dr["task_id"])
                msg = "已归档（默认不出现在搜索中，可在 ZERO 中恢复）"
            elif a == "delete_proposal":
                if actor.get("type") != "user":
                    raise GovError("APPROVAL_REQUIRED", "删除提案必须由用户批准")
                for mid, r in rows.items():
                    if tgt and mid != tgt:
                        continue
                    rv = _bump(c, mid, {"deleted_at": t}, r["revision"])
                    _version(c, mid, "delete", actor, tag)
                    _event(c, mid, "delete", actor, rv - 1, rv, detail=dec.get("reason", ""), task_id=dr["task_id"])
                msg = "已移到回收站（可恢复）"
            elif a == "needs_user_review":
                raise GovError("APPROVAL_REQUIRED", "该结论需要用户选择具体处理方式")
            c.execute("UPDATE memory_review_decisions SET status='applied', decided_at=?, decided_by=?, result_json=? WHERE decision_id=?",
                      (t, actor.get("display", "")[:80], json.dumps({"message": msg}, ensure_ascii=False), did))
            c.execute("UPDATE memory_review_candidates SET status='resolved', updated_at=?, next_check_at=? WHERE candidate_id=?",
                      (t, t + pint(pol, "review.recheck_days") * 86400, dr["candidate_id"]))
    except GovError as e:
        if e.code != "REVISION_CONFLICT":
            raise
        status, err = "conflict", e.message
        with tx(mem) as c:
            c.execute("UPDATE memory_review_decisions SET status='conflict', decided_at=?, result_json=? WHERE decision_id=?",
                      (t, json.dumps({"error": err}, ensure_ascii=False), did))
            c.execute("UPDATE memory_review_candidates SET status='stale', updated_at=? WHERE candidate_id=?", (t, dr["candidate_id"]))
    if dr["task_id"] and gov:
        with tx(gov) as g:
            if status == "applied":
                g.execute("UPDATE governance_tasks SET state='completed', lease_token='', lease_until=0, updated_at=?, revision=revision+1, result_json=? WHERE id=?",
                          (t, json.dumps({"decision_id": did, "action": a, "applied_by": actor.get("display", ""), "message": msg}, ensure_ascii=False), dr["task_id"]))
            else:
                g.execute("UPDATE governance_tasks SET state='failed', lease_token='', lease_until=0, updated_at=?, revision=revision+1, last_error=? WHERE id=?",
                          (t, "REVISION_CONFLICT：" + err, dr["task_id"]))
            _tev(g, dr["task_id"], "applied" if status == "applied" else "conflict", actor.get("display", ""), msg or err)
    return {"ok": status == "applied", "status": status, "message": msg or err, "code": None if status == "applied" else "REVISION_CONFLICT"}


def reject_decision(mem: str, gov: str, did: int, actor: dict, reason: str = "") -> dict:
    t = now()
    with tx(mem) as c:
        dr = c.execute("SELECT * FROM memory_review_decisions WHERE decision_id=?", (int(did),)).fetchone()
        if not dr:
            raise GovError("NOT_FOUND", f"没有找到结论 {did}")
        c.execute("UPDATE memory_review_decisions SET status='rejected', decided_at=?, decided_by=?, result_json=? WHERE decision_id=?",
                  (t, actor.get("display", "")[:80], json.dumps({"reason": reason}, ensure_ascii=False), did))
        c.execute("UPDATE memory_review_candidates SET status='dismissed', updated_at=?, next_check_at=? WHERE candidate_id=?",
                  (t, t + pint(get_policy(gov), "review.recheck_days") * 86400, dr["candidate_id"]))
    if dr["task_id"] and gov:
        with tx(gov) as g:
            g.execute("UPDATE governance_tasks SET state='cancelled', updated_at=?, revision=revision+1, last_error=? WHERE id=?",
                      (t, "用户驳回了提案" + (f"：{reason}" if reason else ""), dr["task_id"]))
            _tev(g, dr["task_id"], "rejected", actor.get("display", ""), reason)
    return {"ok": True, "status": "rejected"}


def user_decide(mem: str, gov: str, cid: int, action: str, actor: dict, new_content: str = "", target_id: int = 0, reason: str = "") -> dict:
    """用户直接在 ZERO 中处理一个候选（不经过 AI）：创建用户结论并立即应用；dismiss = 忽略此候选"""
    view = candidate_view(mem, cid, None)
    t = now()
    if action == "dismiss":
        with tx(mem) as c:
            c.execute("UPDATE memory_review_candidates SET status='dismissed', updated_at=?, next_check_at=? WHERE candidate_id=?",
                      (t, t + pint(get_policy(gov), "review.recheck_days") * 86400, cid))
            tid = c.execute("SELECT task_id FROM memory_review_candidates WHERE candidate_id=?", (cid,)).fetchone()[0]
        if tid and gov:
            with tx(gov) as g:
                g.execute("UPDATE governance_tasks SET state='cancelled', updated_at=?, revision=revision+1, last_error='用户忽略了这个候选' WHERE id=?"
                          " AND state NOT IN ('completed')", (t, tid))
        return {"ok": True, "status": "dismissed"}
    ids = [m["id"] for m in view["memories"]]
    if action == "delete":
        action = "delete_proposal"
    d = {"action": action, "reason": reason or "用户在 ZERO 中处理", "confidence": 1, "target_id": target_id, "new_content": new_content}
    if action in ("merge", "compress", "correct") and not new_content:
        tgt = target_id or ids[0]
        d["new_content"] = next(m["content"] for m in view["memories"] if m["id"] == tgt)
    dec = validate_decision(d, ids)
    expected = {str(m["id"]): m["revision"] for m in view["memories"]}
    with tx(mem) as c:
        tid = c.execute("SELECT task_id FROM memory_review_candidates WHERE candidate_id=?", (cid,)).fetchone()[0]
        did = c.execute("INSERT INTO memory_review_decisions(candidate_id, task_id, action, payload_json, reason, confidence, actor_type, actor_display,"
                        " status, risk_level, expected_json, scope, created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",
                        (cid, tid or "", dec["action"], json.dumps(dec, ensure_ascii=False), dec["reason"], 1, "user", actor.get("display", "用户"),
                         "proposed", "user", json.dumps(expected), view["scope"], t)).lastrowid
    return dict(apply_decision(mem, gov, did, actor), decision_id=did)


# ======================= 统计（界面与 governance_status 共用） =======================
def overview(mem: str, gov: str, mode=None) -> dict:
    out = {}
    sc, ps = ("", []) if mode is None else (" AND scope IN ('all', ?)", [mode])
    if mem:
        with ro(mem) as c:
            q = lambda sql: c.execute(sql + sc, ps).fetchone()[0]
            out["memories"] = q("SELECT COUNT(*) FROM memories WHERE deleted_at=0 AND archived_at=0")
            out["archived"] = q("SELECT COUNT(*) FROM memories WHERE deleted_at=0 AND archived_at>0")
            out["trash"] = q("SELECT COUNT(*) FROM memories WHERE deleted_at>0")
            out["pinned"] = q("SELECT COUNT(*) FROM memories WHERE deleted_at=0 AND pinned=1")
            out["candidates_open"] = q("SELECT COUNT(*) FROM memory_review_candidates WHERE status='open'")
            out["decisions_pending"] = q("SELECT COUNT(*) FROM memory_review_decisions WHERE status='proposed'")
    if gov:
        with ro(gov) as g:
            for st in ("queued", "leased", "running", "review_required", "failed"):
                out["tasks_" + st] = g.execute("SELECT COUNT(*) FROM governance_tasks WHERE state=?" + sc, [st] + ps).fetchone()[0]
            r = g.execute("SELECT started, finished, ok, stats_json, error, trigger FROM scan_runs ORDER BY id DESC LIMIT 1").fetchone()
            out["last_scan"] = dict(r) if r else None
    return out


# ======================= 命令行（供 ZERO 界面调用） =======================
def _cli(argv) -> int:
    import argparse
    ap = argparse.ArgumentParser(prog="zero_gov")
    ap.add_argument("--memory-db", required=True)
    ap.add_argument("--gov-db", required=True)
    ap.add_argument("--actor", default="用户")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("migrate")
    s = sub.add_parser("scan"); s.add_argument("--trigger", default="manual")
    s = sub.add_parser("apply"); s.add_argument("--decision", type=int, required=True)
    s = sub.add_parser("reject"); s.add_argument("--decision", type=int, required=True); s.add_argument("--reason", default="")
    s = sub.add_parser("user-decide"); s.add_argument("--candidate", type=int, required=True); s.add_argument("--action", required=True)
    s.add_argument("--target", type=int, default=0); s.add_argument("--content-file", default=""); s.add_argument("--reason", default="")
    s = sub.add_parser("restore"); s.add_argument("--memory", type=int, required=True); s.add_argument("--revision", type=int, default=0)
    s = sub.add_parser("purge"); s.add_argument("--memory", type=int, required=True)
    s = sub.add_parser("task"); s.add_argument("--id", required=True); s.add_argument("--action", required=True); s.add_argument("--reason", default="")
    sub.add_parser("overview")
    a = ap.parse_args(argv)
    actor = {"type": "user", "display": a.actor}
    try:
        migrate(a.memory_db, "memory")
        migrate(a.gov_db, "governance")
        if a.cmd == "migrate":
            res = {"ok": True}
        elif a.cmd == "scan":
            res = {"ok": True, "stats": scan(a.memory_db, a.gov_db, a.trigger)}
        elif a.cmd == "apply":
            res = apply_decision(a.memory_db, a.gov_db, a.decision, actor)
        elif a.cmd == "reject":
            res = reject_decision(a.memory_db, a.gov_db, a.decision, actor, a.reason)
        elif a.cmd == "user-decide":
            content = Path(a.content_file).read_text(encoding="utf-8") if a.content_file else ""
            res = user_decide(a.memory_db, a.gov_db, a.candidate, a.action, actor, content, a.target, a.reason)
        elif a.cmd == "restore":
            res = {"ok": True, "revision": mem_restore(a.memory_db, a.memory, actor, a.revision)}
        elif a.cmd == "purge":
            mem_purge(a.memory_db, a.memory, actor); res = {"ok": True}
        elif a.cmd == "task":
            task_user_action(a.gov_db, a.id, a.action, actor, a.reason); res = {"ok": True}
        else:
            res = {"ok": True, "overview": overview(a.memory_db, a.gov_db)}
    except GovError as e:
        res = e.to_dict()
    except Exception as e:
        res = {"ok": False, "code": "ERROR", "message": f"{type(e).__name__}: {e}"}
    sys.stdout.write(json.dumps(res, ensure_ascii=False, default=str))
    return 0 if res.get("ok") else 1


if __name__ == "__main__":
    sys.exit(_cli(sys.argv[1:]))
