# ADR-0004：WebDAV 服务商迁移协调协议

- 状态：Accepted
- 日期：2026-07-28

## 背景

同步空间需要在不更换空间 ID、主密钥、设备 ID、事件序号和 Tombstone 的前提下，
从一个 WebDAV 服务迁移到另一个服务。迁移不能复用本地数据目录 helper：前者需要多设备协调、
两套设备本地凭据和两个远端之间的密文镜像，后者只协调单机文件与进程。

WebDAV 没有跨服务商事务。平台安全存储也只提供独立条目的读取、覆盖和删除，
没有跨条目的原子事务。因此正确性必须由版本化远端标记、持久化本地状态、不可变条件写入和
可恢复的凭据提交顺序共同保证。

## 决策

### 1. 迁移对象与兼容边界

迁移只复制当前协议已经写入远端的原始加密字节：

- `metadata.enc`；
- 每个设备的不可变 `events/*.enc`；
- 共享的内容寻址 `blobs/*.enc`。

删除 Tombstone 是加密事件的一种变化类型，随事件原样保留。传输到目标端的内容必须始终是源端
原始密文字节，不得把明文重新序列化、重新加密或生成新主密钥。协调者可以在本机受限缓冲中短暂
解密副本，仅用于验证认证标签、descriptor、事件序号和 Blob 内容地址；明文验证后立即清零，
不得成为迁移输出或持久化内容。

当前版本只在每台设备的 SQLite 中保存下载 Checkpoint。远端虽然已有 `checkpoints/` 集合名、
`SyncDeviceCheckpoint` DTO 和 `SyncObjectType.Checkpoint`，但没有规范文件路径或任何远端读写实现。
本次迁移保持本地 `sync_checkpoints`、Outbox 和设备序号不变；远端 checkpoint 对象数量定义为零。
发现 `checkpoints/` 下存在未知文件时必须以协议冲突阻断迁移。未来发布远端 Checkpoint 时，
必须另行版本化路径和读写语义，再纳入强类型库存，不能由迁移器猜测。

迁移要求所有仍有效的设备运行支持本 ADR 的版本。设备撤销尚未实现，因此协调者不得按超时忽略
离线或旧版本设备；用户只能等待、取消，或先完成后续的设备撤销能力。

### 2. 计划身份、epoch 与远端标记

每个计划具有随机 `PlanId` 和单调递增 `Epoch`。加密 intent 包含：

- 协议版本、PlanId、SpaceId、Epoch 和发起设备；
- 源与目标 endpoint/root/证书固定规则的规范摘要；
- 经过用户确认的目标 endpoint/root/证书固定非秘密元数据；
- 全部必需设备 ID。

用户名、密码、恢复码和主密钥永不进入计划或日志。目标用户名与密码由每台设备独立输入。

迁移控制对象使用新的 `SyncObjectType.ProviderMigration`，继续沿用现有 AES-256-GCM 信封、
主密钥和严格 AAD。控制对象位于独立布局：

```text
spaces/{space}/migrations/{plan}/intent.enc
spaces/{space}/migrations/{plan}/ready/{device}.enc
spaces/{space}/migrations/{plan}/freeze.enc
spaces/{space}/migrations/{plan}/commit.enc
spaces/{space}/migrations/{plan}/committed/{device}.enc
spaces/{space}/migrations/{plan}/terminal.enc
spaces/{space}/migrations/{plan}/rollback.enc
spaces/{space}/migrations/{plan}/rolled-back/{device}.enc
spaces/{space}/migrations/{plan}/completed.enc
```

`terminal.enc` 是 `Rollback` 与 `Completed` 的唯一裁决点，其加密 payload 只能是这两种决定之一。
所有设备先在旧端使用 `If-None-Match: *` 条件创建该对象；条件竞争的失败方读取并跟随已经存在的
决定。`rollback.enc` 与 `completed.enc` 是裁决结果的可审计别名，不参与选择，且不得同时存在。
这样完成与回滚即使由不同设备并发发起，也只能得到一个全局终态。

每个路径只条件创建一次。重复请求读取并解密既有对象，只有语义完全一致才视为幂等；
同路径同类型但不同计划内容、终态裁决与别名不一致均是协议冲突。旧 epoch、第二个并发活动计划和
不属于必需设备集合的 ack 均拒绝。

所有控制标记都以相同语义写入旧端和新端，单边成功后的重试只补写缺失一侧。提交前旧端是发现
迁移和决定回滚的权威入口；commit 后两端的相同不可变决定共同支持已切换与未切换设备恢复。
旧端永久保留只读标记和原密文数据，使离线设备能够发现迁移。

### 3. 状态机与设备协调

共享 Application 状态机为：

```text
Draft
  -> PreflightTarget
  -> PreparingDevices
  -> WaitingForDeviceAcks
  -> Frozen
  -> MirroringCiphertext
  -> VerifyingTarget
  -> Committing
  -> WaitingForDeviceCommits
  -> Completed

需要目标凭据的参与设备 -> TargetCredentialsRequired
任一可恢复阶段 -> RollingBack -> RolledBack
无法确认任一权威端 -> Failed（阻断写入并要求人工恢复）
```

发起设备先同步并排空自己的 Outbox，再暂存源/目标凭据并验证目标 TLS、认证、目录和条件写入能力。
随后在旧端写入 intent。其他设备在普通同步上传前检查 intent；发现新的活动计划后，完成当前已开始的
读取但停止后续旧端上传，持久化阻断状态，并要求用户输入该设备自己的目标凭据。

每台设备在目标连接验证成功、旧端 Outbox 排空后，记录本地最高序号、最高已上传序号和所有本地
下载 Checkpoint，写入 ready ack。所有必需设备 ready 前不能冻结。协调者写入 freeze 后，
所有参与设备都保持写入阻断。

目标验证完成后，协调者在两端写入 commit。每台设备只在看到相同 PlanId/Epoch 的 commit、
且本机已验证目标凭据后提交 active 凭据；随后从新端执行完整增量检查并写 committed ack。
任一设备提交失败时，所有未提交设备继续阻断，已提交设备只写新端，状态保持可见的
`WaitingForDeviceCommits`，不会恢复旧端写入。全部 committed 后由发起设备在旧端提出 `Completed`
终态；若并发回滚已经赢得 `terminal.enc`，发起设备必须跟随回滚，否则镜像 `completed.enc` 别名并
恢复正常同步。

### 4. 密文库存、镜像和验证

远端迁移接口只接受强类型对象引用，不能接受调用方提供的任意相对路径。库存按以下顺序生成：

1. metadata；
2. 按 DeviceId 排序、再按 Sequence/EventId 排序的事件；
3. 按 keyed Blob ID 排序的 Blob；
4. 验证每个 checkpoint 集合为空。

每个引用记录规范身份、长度和源端 ETag。协调者逐对象 GET 源端原始密文，计算 SHA-256，
再对目标执行 `If-None-Match: *` 条件 PUT。目标已存在时必须 GET 并逐字节固定时间比较：
相同则幂等跳过，不同则阻断。ETag 只作诊断，不能代替内容比较。

镜像后重新枚举目标，逐项比较规范身份、长度和 SHA-256，并校验每个加密信封的 descriptor
仍与对象路径一致；不能只比较文件数量。迁移逐对象顺序执行并及时清零缓冲，遵守现有 90 MiB
加密信封上限。第一版不使用 WebDAV `COPY`，也不自动调用 `DELETE` 清理旧端。

### 5. 本地持久化与凭据事务

SQLite v8 使用专用计划表和设备状态表，只保存 PlanId、SpaceId、Epoch、非秘密远端摘要、状态、
设备水位、进度和稳定诊断码。不得在 SQLite 中新增 endpoint、root、用户名、密码或证书字段。
状态更新继续进入现有有界单写队列和 SQLite 事务。

平台安全存储保留兼容的 active key，并增加计划级暂存 key：

```text
sync/webdav/{space}
sync/webdav/{space}/migration/{plan}/source
sync/webdav/{space}/migration/{plan}/target
```

开始时把 active 原始安全存储 bundle 复制到 source，再独立写 target。提交前 active 始终为旧端。
提交时先确认 active 仍与 source 一致，再把 target 覆盖到 active，并读回逐字节验证。
失败则从 source 恢复 active 并再次读回验证。任何无法验证的恢复进入 `Failed`，不得静默选择端点。

完成后删除 target 暂存，但保留 source 旧凭据，直到未来明确、可审计的保留期清理动作。
回滚确认后恢复 source 并删除失败目标暂存。由于平台存储没有跨条目事务，本地 SQLite 阶段与
source/target 暂存 key 共同承担崩溃恢复；启动时按 PlanId 重放提交或回滚，不能枚举或记录秘密。

### 6. 取消、回滚和重放

冻结前取消提出 rollback 并清理目标暂存，旧端继续权威。冻结后取消必须进入 RollingBack：
先在旧端竞争 `terminal.enc`；只有 `Rollback` 获胜时才在旧、新端写 rollback 别名，所有已提交设备
从 source 恢复 active，完成一次旧端增量检查并写 rolled-back ack。全部必需设备确认后为
RolledBack。若 `Completed` 已先获胜，陈旧设备不得再写 rollback，而应完成本机目标凭据提交。

协调者崩溃后，原发起设备使用同一 PlanId/Epoch 从 SQLite 和远端标记恢复。第一版不允许其他设备
接管 freeze、镜像、commit 或 completed：在设备撤销和带租约的协调者选举完成前，跨设备接管会
产生双协调者风险。其他设备只能等待原发起设备恢复或发起全局回滚。不可变条件 PUT、语义一致的
marker 重放和同字节对象跳过保证镜像可重复；更高 epoch 已存在时，旧计划不得重新进入活动状态。

## 后果

- Windows 与 macOS 使用同一 Application 状态机、协议 DTO、SQLite 状态和界面语义；
  平台差异仅限安全存储与系统窗口。
- 迁移期间不会覆盖其他设备事件，也不会把密码或内容明文写入数据库、日志或远端控制对象。
- 永久离线设备会安全阻塞迁移，直到设备撤销功能存在；这是防止双主写入的有意限制。
- 旧端默认保留，远端清理是独立的后续功能。
- 当前全量 `byte[]` WebDAV API 会使大 Blob 迁移出现受限内存峰值；后续可在不改变状态机的前提下
  增加有界流式 GET/PUT。
