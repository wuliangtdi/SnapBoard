# ADR-0003：System.Text.Json 源生成

- 状态：Accepted
- 日期：2026-07-26

## 背景

同步协议需要稳定、可版本化并兼容 Native AOT 的 JSON 序列化。反射型序列化会增加裁剪风险，任意类型元数据也扩大安全攻击面。

## 决策

只使用 .NET 内置 System.Text.Json。所有同步 DTO 登记到 `SyncJsonContext`，调用带 `JsonTypeInfo<T>` 的重载。禁止 Newtonsoft.Json、动态类型和类型名反序列化。

## 后果

- AOT 编译期能发现遗漏的协议类型。
- DTO 新增或修改必须同时更新源生成上下文与兼容测试。
- 不为通用便利引入第二套 JSON 规则。
