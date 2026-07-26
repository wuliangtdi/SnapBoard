# ADR-0001：直接使用 Microsoft.Data.Sqlite

- 状态：Accepted
- 日期：2026-07-26

## 背景

SnapBoard 需要 Native AOT、零未解释裁剪警告、低于 100 MB 常驻内存，并需要直接控制 WAL、FTS5、迁移和分页投影。候选包括 SqlSugar、EF Core 和 Microsoft.Data.Sqlite。

## 验证

- `SqlSugarCoreNoDrive 5.1.4.216` 在官方整程序集 `rd.xml` 下发布 Native AOT 时解析未携带的 MySqlConnector 等驱动，编译失败。
- `SqlSugarCoreNoDrive.Aot 5.1.4.186` 携带多个无关数据库驱动，并产生 IL2104、IL3053、IL3000 等错误。
- EF Core 10 的 Native AOT 查询预编译仍被微软标记为高度实验性。
- Microsoft.Data.Sqlite 直接发布 `osx-arm64` Native AOT 成功，依赖图更小。

## 决策

正式数据层使用 Microsoft.Data.Sqlite、参数化 SQL、显式投影和手工映射。仓储接口隔离 Provider 类型，写操作通过单写队列执行。

## 后果

- 获得最清晰的 AOT 和性能行为。
- 迁移、SQL 和映射需要自行维护并测试。
- 只有出现经过测量的维护瓶颈时，才评估编译期代码生成方案。
- 不通过压制第三方 AOT 警告恢复 SqlSugar。
