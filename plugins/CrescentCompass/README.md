<p align="center">
  <img src="images/icon.png" width="112" alt="新月罗盘图标">
</p>

<h1 align="center">新月罗盘</h1>

<p align="center">
  面向国服 Dalamud API 15 的新月岛综合辅助插件
  <br>
  将普通宝箱、魔法罐寻宝、FATE、CE、场景提示和导航集中在一处
</p>

<p align="center">
  <a href="#地图与普通宝箱">地图与宝箱</a> ·
  <a href="#魔法罐寻宝">魔法罐寻宝</a> ·
  <a href="#fate-与-ce">FATE 与 CE</a> ·
  <a href="#自动事件与战斗接管测试">自动事件（测试）</a> ·
  <a href="#内置路线与路线录制">路线录制</a> ·
  <a href="#事件速查">事件速查</a> ·
  <a href="#导航与显示">导航与显示</a>
</p>

## 功能一览

| 功能 | 可以做什么 |
| --- | --- |
| **普通宝箱** | 显示铜／银宝箱固定点位，长期记录已确认存在的宝箱，调查并估算区域剩余数量 |
| **魔法罐寻宝** | 解析方向与距离提示，筛选固定候选点，跟踪寻宝轮次并显示实例预测 |
| **FATE 与 CE** | 查看实时状态、剩余时间、魂晶奖励、触发怪、触发解禁时间和众包信息 |
| **收藏提醒** | 事件刷新时发出游戏内提醒；失焦时可改用 Windows 横幅，并独立控制声音 |
| **自动事件与战斗接管（测试）** | 按规则选择 FATE／CE，自动导航、下坐骑、选择目标，并交给已配置的战斗插件接管 |
| **内置与用户路线** | 随插件提供基础路线；推荐玩家自行录制完整路线和跳跃动作，用户路线优先 |
| **场景标注** | 在游戏画面中显示宝箱、候选点、FATE、CE、魔法罐和怪物信息 |
| **地图导航** | 从地图或速查窗口直接前往，支持 vnavmesh、传送、坐骑、脱困和怪物绕行 |
| **配置迁移** | 将全部用户设置、录制路线和校准数据导出或导入为 JSON，并在导入前自动备份 |

## 地图与普通宝箱

地图汇总当前事件、宝箱、魔法罐候选、玩家和队友位置。探知到的普通宝箱会保存至开启，更换地图也不会清除；静态点位、已确认宝箱和场景标签可以分别开关。

<table>
  <tr>
    <td width="52%" align="center"><strong>地图总览</strong></td>
    <td width="48%" align="center"><strong>宝箱调查与确认状态</strong></td>
  </tr>
  <tr>
    <td align="center"><img src="images/overview-map.png" alt="地图上的事件、候选点与玩家位置"></td>
    <td align="center"><img src="images/treasure-status.png" alt="宝箱数量调查与已确认宝箱统计"></td>
  </tr>
</table>

## 魔法罐寻宝

收到撒娇罐提示后，新月罗盘会用方向和距离逐步缩小范围，在地图上绘制提示边界并列出剩余候选。错误输入可以撤销，也可以切换关注点或重置本轮。

<table width="100%">
  <tr>
    <td colspan="2" align="center">
      <img src="images/treasure-hunt.png" width="629" alt="新月罗盘魔法罐寻宝">
      <br>
      <sub>提示范围、候选列表与当前关注点</sub>
    </td>
  </tr>
  <tr>
    <td width="38%" align="center" valign="top">
      <strong>场景候选</strong>
      <br><br>
      <img src="images/treasure-candidate-scene.png" width="166" alt="宝藏候选的距离与高差">
      <br>
      <sub>显示距离与高差</sub>
    </td>
    <td width="62%" align="center" valign="top">
      <strong>魔法罐预测</strong>
      <br><br>
      <img src="images/magic-pot-forecast.png" width="338" alt="魔法罐预测时间与来源">
      <br>
      <sub>显示预计时间、剩余时间与数据来源</sub>
    </td>
  </tr>
</table>

## FATE 与 CE

事件页同时呈现 FATE、魔法罐预测和 CE。状态会随报名、准备、战斗和结束过程更新，并用彩色短标签标出魂晶奖励。

<table>
  <tr>
    <td width="48%" align="center"><strong>当前事件</strong></td>
    <td width="52%" align="center"><strong>游戏场景标注</strong></td>
  </tr>
  <tr>
    <td align="center"><img src="images/event-overview.png" alt="当前可见 FATE、CE 与魔法罐预测"></td>
    <td align="center"><img src="images/fate-scene-label.png" alt="FATE 场景标注"></td>
  </tr>
</table>

收藏的事件每次出现只提醒一次。游戏在前台时使用 Dalamud 通知和可选醒目横幅；失焦或最小化时可以显示短暂的 Windows 横幅。

## 自动事件与战斗接管（测试）

自动事件可以按 CE 优先、FATE 优先、距离或魔法罐优先选择目标，并应用最低剩余时间、FATE 最大进度和参与列表等规则。导航抵达事件后会选择事件内目标、进入职业攻击距离并下坐骑，再启动所选战斗托管。

- BossmodRebornCN 可接管机制移动；接管后新月罗盘会停止 vnavmesh 追击，避免两个插件争夺移动控制。
- AEAssistV3、PromeRotation、Rotation Solver Reborn 或 BossmodRebornCN 可作为技能循环提供方。
- FATE 与 CE 的停止点会在事件中心附近选择随机可达位置；录制路线只在运行时替换最后一个停止点，不修改保存的数据。
- 事件结束后会原地等待消失确认和奖励结算，再返回等待点或进入魔法罐寻宝流程。

自动事件是可选功能，默认关闭。启用前应在设置中确认依赖状态、参与范围和战斗提供方。

## 内置路线与路线录制

0.2.0 随插件提供 39 条南北岛 FATE／CE 基础路线，用于首次安装和没有用户路线时的缺省回退。角色移动、网络延迟、vnavmesh 网格与个人路线偏好可能不同，**更推荐用户从实际传送落点自行录制路线**。导航会优先使用同一事件的用户录制路线；没有用户路线时使用内置路线，二者都不可用时再交给 vnavmesh 普通寻路。

路线从水晶传送落点开始录制，到 FATE／CE 标准终点自动结束并保存。录制过程会保存玩家主动跳跃，回放时在对应位置执行跳跃。设置页会标注路线来源：

- **内置**：随插件更新的基础路线，可测试但不能删除，主要用于缺省回退。
- **用户**：保存在个人配置中，可测试和删除；同一事件存在用户路线时覆盖内置路线。

完整配置导出只保存用户路线和个人设置；内置路线由插件版本提供，不会重复写入个人配置。

## 事件速查

CE 速查列出触发方式、触发怪、加速触发信息、触发解禁时间、众包状态和已知位置。FATE 速查覆盖南北岛事件。两者都支持搜索、收藏、区域筛选和从已知坐标直接前往。

### CE 关注与触发怪速查

<p align="center">
  <img src="images/ce-reference.png" width="100%" alt="CE 关注、触发状态与位置速查">
</p>

### FATE 关注与位置速查

<p align="center">
  <img src="images/fate-reference.png" width="760" alt="FATE 关注与位置速查">
</p>

## 导航与显示

导航会比较直达、亚返回与水晶传送路线，并把最终路径绘制在地图上。启用 vnavmesh 后支持移动中上坐骑、卡住检测、跳跃恢复和录制路线回放；南北岛均可按高等级敌对生物的警戒范围调整路线。

<table>
  <tr>
    <td width="44%" align="center"><strong>路线预览</strong></td>
    <td width="56%" align="center"><strong>地图标记尺寸</strong></td>
  </tr>
  <tr>
    <td align="center"><img src="images/navigation-route.png" alt="地图导航路线"></td>
    <td align="center"><img src="images/map-marker-settings.png" alt="地图标记尺寸设置"></td>
  </tr>
</table>

<details>
<summary><strong>使用说明</strong></summary>

- vnavmesh 是可选依赖；未安装时仍可使用地图、宝箱、事件、提醒和场景标注。
- 自动移动和录制路线回放需要 vnavmesh；战斗接管需要启用相应的第三方战斗插件。
- 魔法罐预测只用于寻宝提示，实际刷新前不会触发收藏 FATE 提醒。
- 使用寻宝功能期间会暂时隐藏已刷新普通宝箱的场景标签，减少画面干扰。
- 地图标记、场景标注和提醒均可在“新月罗盘设置”中独立开关或调整。
- “外观”设置页可导出或导入完整 JSON 配置；导入前会在插件配置目录生成备份。

</details>

## 许可

项目使用 AGPL-3.0 许可。第三方实现与数据来源见 [第三方来源说明](../../THIRD_PARTY_NOTICES.md)。
