# SnapBoard 安全设计

## 1. 保护对象

剪贴板可能包含密码、令牌、私钥、个人信息、内部代码和图片，因此默认按敏感数据处理。需要保护本地历史、同步密文、WebDAV 凭据、设备密钥、日志和崩溃报告。

## 2. 信任边界

- 操作系统账户和系统凭据存储位于可信边界内。
- WebDAV 服务、网络和其他同步设备均可能不可信。
- 剪贴板来源应用可能提供恶意 HTML、RTF、文件名或超大载荷。
- UI 预览必须把内容当数据处理，不能执行脚本、外部资源或任意协议。

## 3. 本地数据

- 数据目录仅当前用户可读写。
- SQLite 使用参数化 SQL、外键、WAL 和明确迁移。
- 大对象写入临时文件后原子重命名，数据库只在文件成功后提交引用。
- SQLCipher 是否默认启用由 AOT、内存和恢复测试决定；未启用时必须在设置中明确本地保护边界。
- 清理操作使用 Tombstone/事务，防止同步设备把已删除内容复活。

## 4. 敏感内容过滤

- 识别平台 transient/confidential 格式和常见密码管理器标记。
- 支持按来源应用完全忽略或只保存纯文本。
- 支持暂停、自动过期、立即清空和最大容量。
- 日志、遥测、异常和测试快照不得包含正文、图片字节、WebDAV 密码和主密钥。

## 5. WebDAV 与端到端加密

- 仅允许 HTTPS；不提供忽略所有证书错误的开关。
- 自签名证书使用明确的证书指纹固定。
- WebDAV 凭据和内容主密钥彼此独立，并存入 DPAPI/Credential Locker、Keychain 或 Secret Service/KWallet。
- 事件分片和 Blob 使用认证加密；协议版本、SpaceId、DeviceId 和序列号进入附加认证数据。
- 每台设备只写自己的目录；不可变对象配合 ETag 条件请求避免覆盖。
- 设备加入、恢复码、撤销和密钥轮换必须有审计事件，但审计不含正文。

## 6. 序列化

- 只使用 System.Text.Json 源生成上下文。
- 不允许 Newtonsoft.Json、`TypeNameHandling`、任意类型反序列化和运行时程序集扫描。
- 所有协议 DTO 具有版本、大小上限和字段验证。
- 解密成功不代表内容可信；解压比、长度、哈希和 MIME 仍需校验。

## 7. 依赖安全

NuGet restore 开启漏洞审计并将警告视为错误。Microsoft.Data.Sqlite 10.0.10 的原始传递依赖会选择已公告的 SQLitePCLRaw 2.1.11，仓库显式固定 `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`，测试要求 SQLite >= 3.50.2。

Dependabot 每周检查 NuGet 和 GitHub Actions。升级原生依赖后必须重新执行三个平台的 AOT、数据库版本和基本 CRUD 测试。

## 8. 报告漏洞

GitHub 仓库创建后启用 Private Vulnerability Reporting。正式发布前在 README 和 `SECURITY.md` 补充受支持版本和私密联系渠道，禁止要求报告者在公开 Issue 中粘贴敏感剪贴板样本。
