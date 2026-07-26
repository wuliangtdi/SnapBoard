# ADR-0002：WebDAV 不可变事件同步

- 状态：Accepted
- 日期：2026-07-26

## 背景

用户需要使用自有 WebDAV、Nextcloud 或 NAS 同步。直接同步 SQLite/WAL 会在多设备写入、锁、部分上传和服务端实现差异下损坏数据。

## 决策

每台设备只写自己的远端目录。客户端上传端到端加密的不可变事件分片、Checkpoint 和内容寻址 Blob，使用 PROPFIND 枚举、GET 下载、条件 PUT 创建。核心正确性不依赖 WebDAV LOCK。

## 后果

- 设备可离线工作并幂等重放。
- 需要 Outbox、游标、Tombstone、Checkpoint 和远端垃圾回收协议。
- WebDAV 没有统一推送，默认同步延迟为数秒到数十秒。
- 服务端只能看到密文对象和访问元数据，不能读取剪贴板正文。
