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
- 大对象写入临时文件并强制落盘后原子重命名，数据库只在文件成功后提交哈希、大小、MIME、引用计数和相对路径；原始图片不长期放入数据库或列表内存。
- TIFF 缩略图只在 Infrastructure 后台使用 `BitMiracle.LibTiff.NET` 解码，像素数上限为 40,000,000；损坏、尺寸溢出或不支持的 TIFF 保留原图但不生成缩略图，临时编码/栅格缓冲在使用后清零。UI 与快速列表不直接持有 TIFF 原图。
- 孤儿扫描延迟到启动两分钟后在后台执行，候选文件至少保留 24 小时；每批删除前在单写队列内按完整相对路径重新查询数据库，无法证明无引用时保留文件。
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

## 7. macOS 权限、身份与 Keychain

- 辅助功能状态刷新只调用无提示的预检 API；只有设置页中用户主动执行“请求权限”或“打开系统设置”命令时，平台服务才请求 TCC 或跳转系统设置。应用启动、窗口打开和后台轮询不得自动弹出权限提示。
- 权限拒绝、撤销、目标恢复失败或事件注入失败不阻止剪贴板写入；结果进入受限模式并提示用户手动粘贴。UI 只消费平台无关状态，不直接引用 Accessibility、CoreGraphics 或 AppKit。
- App Bundle Identifier 固定为 `com.wuliangtdi.snapboard`。开发裸程序明确标记为开发身份，不把它的 TCC/登录启动状态当成正式 Bundle 状态；撤销和重新授权必须使用相同 Bundle ID 与稳定 Developer ID 签名身份实测。
- NSPasteboard 不提供可靠来源时，macOS 必须把 PID、进程名、路径、AUMID 和 Package Family 留空并把归属记为 Unknown；不得用当前前台应用猜测，也不得注册 AppsFolder 或其他 Windows 身份解析器。
- `MacOSKeychainSecretStore` 使用 Security.framework Generic Password 项，Service 固定为 `com.wuliangtdi.snapboard`，账户名经过长度与控制字符校验，单项上限 64 KiB。新增、读取、覆盖和删除返回结构化状态，临时明文缓冲区使用后清零，不回退为 JSON、plist 或其他明文凭据文件。
- 正式包的 entitlement 集为空并启用 Hardened Runtime；本机 ad-hoc 验证包因 Native AOT 原生库加载仅使用独立的 `disable-library-validation` entitlement。正式发布不得沿用该本地 entitlement，且 Developer ID Application、Developer ID Installer 和公证凭据必须同时存在才允许进入正式签名路径。

本轮当前 App Bundle 的辅助功能状态为已授权，Keychain 临时密钥新增/读取/删除已通过。为避免未经确认修改系统安全和登录项，没有执行同一身份撤销后重新授权或登录启动开关；本机也没有 Developer ID 身份和公证凭据，因此只验证了 ad-hoc Hardened Runtime、未签名 PKG 和公证跳过路径。

## 8. Windows 历史、策略与 Credential Manager

- 历史数据库使用版本化 Schema、参数化 SQL、显式列投影和单写事务；Application、Domain 与 UI 不引用 `Microsoft.Data.Sqlite` 类型。`quick_check` 失败时先把数据库及可用的 WAL/SHM 文件复制到时间戳恢复目录，再重建空库，并只在诊断结果中记录文件名和异常类型，不记录剪贴板正文。
- 采集责任链在计算哈希和持久化前拒绝本应用来源、常见密码管理器进程、Windows/macOS transient/confidential 格式、应用黑名单和超限载荷；仅文本规则会移除 HTML、RTF、图片和文件表示。测试和日志只使用生成内容，不输出正文、图片字节或敏感格式载荷。
- `WM_CLIPBOARDUPDATE` 回调只快照剪贴板序列号和数值 PID，不在消息线程读取路径、包清单或图标。来源 EXE、AUMID、Package Family 和归属依据使用参数化字段持久化，不进入正文日志；AppsFolder 身份有长度/字符边界，PIDL、进程句柄、`HICON`、DC 和位图均走显式释放路径。
- `WindowsCredentialSecretStore` 复用 `IPlatformSecretStore`，使用 Win32 Credential Manager Generic Credential，目标前缀固定为 `com.wuliangtdi.snapboard/`。名称拒绝控制字符并限制 UTF-8 长度，密钥大小受系统 Credential Blob 上限约束。
- P/Invoke 使用 Native AOT 友好的 `LibraryImport`；`CredRead` 返回的系统明文在复制后原位清零再 `CredFree`，`CredWrite` 的临时托管副本在调用后清零。读取返回给调用方的缓冲仍由调用方在使用后负责清零。
- 新增、读取、覆盖、删除和不存在状态已通过真实 Credential Manager 临时项测试；拒绝、取消、无登录会话、无效名称和超限输入已覆盖确定性映射。测试最终清理临时项，不把 WebDAV 凭据、恢复码或主密钥写入 JSON、注册表明文、日志或测试快照。

当前本地历史数据库和外部 Blob 尚未启用静态加密；保护边界仍是当前 Windows 用户账户和文件系统 ACL。下一阶段的 WebDAV 同步必须使用独立内容主密钥与端到端认证加密，不能把当前本地明文文件直接上传。

## 9. 依赖安全

NuGet restore 开启漏洞审计并将警告视为错误。Microsoft.Data.Sqlite 10.0.10 的原始传递依赖会选择已公告的 SQLitePCLRaw 2.1.11，仓库显式固定 `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`，测试要求 SQLite >= 3.50.2。`BitMiracle.LibTiff.NET 2.4.660` 只进入 Infrastructure，没有额外包依赖；2026-07-28 的直接/传递漏洞审计、Release 构建和 `osx-arm64` Native AOT 均通过。任何后续 TIFF 解码器升级仍需重跑损坏输入、像素上限、临时文件和 AOT 验证。

Dependabot 每周检查 NuGet 和 GitHub Actions。升级原生依赖后必须重新执行三个平台的 AOT、数据库版本和基本 CRUD 测试。

## 10. 报告漏洞

GitHub 仓库创建后启用 Private Vulnerability Reporting。正式发布前在 README 和 `SECURITY.md` 补充受支持版本和私密联系渠道，禁止要求报告者在公开 Issue 中粘贴敏感剪贴板样本。
