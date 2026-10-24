# 解压后试一遍

从 [Release](https://github.com/shitou2226/refrigeration-hmi/releases/tag/v0.1.0-preview.1) 下载 Windows x64 ZIP，完整解压，双击 `RefrigerationHmi.exe`。便携包自带运行时，不用安装 SDK。

1. 首次打开会自动开始模拟采集。观察温度、压力、趋势和记录数一起变化。
2. 在“故障演示”选择“排气高压故障”，到报警页查看发生记录。确认报警后，切回正常运行，再看恢复时间。
3. 选择“通信中断故障”，观察质量变为 Bad、数值为空、趋势断开；切回正常后继续采集。
4. 点击“启动 TCP 模拟器”，把数据来源改成“本机 Modbus TCP”。停止并重启模拟器，观察通信失败和恢复。
5. 在历史页查询刚才的时间范围，导出 CSV，核对失败采集的数值为空。

数据保存在程序目录的 `AppData/`。再次打开会继续使用这个目录；需要清空演示数据时，先关闭程序，再备份并处理该文件夹。

这些演示使用模拟值和本机 TCP，尚未接入实际 PLC 或制冷机组。完整点表见 [首页](https://github.com/shitou2226/refrigeration-hmi)。
