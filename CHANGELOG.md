# Changelog

## [2.29.0](https://github.com/qsy123-coder/JASM/compare/v2.28.0...v2.29.0) (2026-09-24)


### Features

* **core:** 新增 CursorRecenterGuard，判定光标是不是被游戏挪到了窗口中心 ([ab2fa5f](https://github.com/qsy123-coder/JASM/commit/ab2fa5fde6a707d01bdf97e92e779033758f295f))
* **core:** 新增提权助手「归还前台」命令 4 的协议 ([8466de8](https://github.com/qsy123-coder/JASM/commit/8466de86b4e6ced32e094966c7a83274a5faaf56))
* **core:** 新增送键闸门 RefreshSendPacer（两发 F10 之间至少隔 2.5 秒） ([36507c4](https://github.com/qsy123-coder/JASM/commit/36507c41d52c7c5328d26a8c4bc59d8c3aab0e62))
* **elevator:** 新命令 4 —— 把前台交还给指定窗口 ([b00e12c](https://github.com/qsy123-coder/JASM/commit/b00e12c29bb06bf713d310e0b3b5a852e710d994))
* **input:** ForegroundWindowActivator 支持切前台前先注入一次空输入 ([6f7abec](https://github.com/qsy123-coder/JASM/commit/6f7abec8b14126d15744d087383d98d82b337edd))
* **input:** 合成输入加一次零位移鼠标移动，用于解锁前台锁 ([5644613](https://github.com/qsy123-coder/JASM/commit/5644613dad076c7f36c1f1f29626c3e057d3849b))
* **overlay:** 列表加名字以供滚动 + 选中行高亮 ([13faa89](https://github.com/qsy123-coder/JASM/commit/13faa89c0bab92b21e18d747f8673a1c95542a86))
* **overlay:** 勾选刷新送完键把前台收回浮窗 ([021607f](https://github.com/qsy123-coder/JASM/commit/021607f5e3a28720ef2a1ef14065acc2ec333274))
* **overlay:** 把导航热键接到浮窗的选中 / 切换 / 刷新 ([386d1d5](https://github.com/qsy123-coder/JASM/commit/386d1d52270c76785e48902e6a2aa21b9fc76968))
* **overlay:** 把浮窗句柄交给刷新协调器 ([acaa069](https://github.com/qsy123-coder/JASM/commit/acaa069215cfa2fd2f1aaa70bbda68b9e257267e))
* **overlay:** 探针增加「光标是否压在本进程窗口上」的只读查询 ([77ed3db](https://github.com/qsy123-coder/JASM/commit/77ed3dbecc1eb622cfa3fe62cfd73994b949aa29))
* **overlay:** 注册器扩到唤出键 + 四个导航键（导航键随显隐注册） ([adebfea](https://github.com/qsy123-coder/JASM/commit/adebfead763360cfe44ec30535d19a560e17b1d7))
* **overlay:** 现场补记「点击门槛」—— 看得见点不到是输入归属问题 ([07d8896](https://github.com/qsy123-coder/JASM/commit/07d8896e331a9309782dd8d47f4e2f93e4a95055))
* **overlay:** 行 VM 加 IsSelected，供键盘选中的高亮 ([a00e9f7](https://github.com/qsy123-coder/JASM/commit/a00e9f7c2f01cb4126449964aa4b1307501d9a1f))
* **overlay:** 送键前过闸门，补发不再砸进游戏的重载窗口 ([449f8f5](https://github.com/qsy123-coder/JASM/commit/449f8f5b65e4c46e1d349c3e4091a8847258245c))
* **overlay:** 选中行变化时滚进视野 ([666dd81](https://github.com/qsy123-coder/JASM/commit/666dd81fdaf2a14a833a7596547694b5cbe3c1ad))
* **overlay:** 键盘选中行（上下移动 / 回车切换），过滤后重定位 ([9e39ee6](https://github.com/qsy123-coder/JASM/commit/9e39ee660b0cfe235a99e777ca858c844c7d41b1))
* **winui:** SendKeyAsync 增加「送完键把前台交给谁」参数 ([62f9567](https://github.com/qsy123-coder/JASM/commit/62f9567a1dd067006fd1b82a768ddb115a83235c))
* **winui:** 新增请助手交还前台的调用（命令 4），并按版本门控 ([6a0c25d](https://github.com/qsy123-coder/JASM/commit/6a0c25d568a7053c65d559e6284b620dbbec0da5))
* **winui:** 送完键按调用方要求交还前台（提权支走助手，直发支自己来） ([9787535](https://github.com/qsy123-coder/JASM/commit/9787535c5b29cd58822a145ca911a731362424ae))


### Bug Fixes

* **elevator:** 目标刷新定位窗口时带上 logger，复用候选窗口日志 ([b4494a0](https://github.com/qsy123-coder/JASM/commit/b4494a05b0d6f8f4dd0ee0f3fe46b5dff23393f0))
* **input:** 送键前等用户松开修饰键，别把 F10 发成 Alt+F10 ([39aebf3](https://github.com/qsy123-coder/JASM/commit/39aebf30314f9bee9240833a7653a25e332869c8))
* **keyinput:** 找游戏窗口改挑面积最大的渲染主窗口，并逐条记录候选 ([88e6905](https://github.com/qsy123-coder/JASM/commit/88e6905aa4c78603aa15422f801276d0b4f192de))
* **keyinput:** 提权助手送键补记目标窗口与交办前后台窗口 ([a789e80](https://github.com/qsy123-coder/JASM/commit/a789e80eabbbb8cd96c6e91bd9db120685dc6060))
* **overlay:** 刷新热键给候选，R 被占时退到 Space ([c80b079](https://github.com/qsy123-coder/JASM/commit/c80b0799a560e267d192e8b9beede5e311a7506d))
* **overlay:** 前台被抢走后一秒内拿回来（光标在浮窗上时） ([6931e83](https://github.com/qsy123-coder/JASM/commit/6931e83929c0bd95cbe81e33e53eae999a5dfecd))
* **overlay:** 层级现场改走真 Z 序链，EnumWindows 的枚举位置会漏掉 IME 那一块 ([093be97](https://github.com/qsy123-coder/JASM/commit/093be974d4a5f6d0b99369491b64169deebb0dd2))
* **overlay:** 撤掉无效的置顶重申，订正被实测推翻的注释 ([1677b61](https://github.com/qsy123-coder/JASM/commit/1677b6163eb1fbd51eb6bcf17d3808d9928f5866))
* **overlay:** 置顶自愈改为每秒无条件重申，并定期把层级现场写进日志 ([9fdf9c8](https://github.com/qsy123-coder/JASM/commit/9fdf9c8f7b7396e67f11424c89b454e1329f6588))
* **overlay:** 送键切前台前记下光标，交还前台后若被游戏挪到窗口中心就放回原位 ([d6f049e](https://github.com/qsy123-coder/JASM/commit/d6f049e433db04a4b9e44eae4b8b2fa1bed4748b))


### Miscellaneous

* **elevator:** FileVersion 抬到 4.0.0.0（能力标记：认识归还前台 4） ([ef04c01](https://github.com/qsy123-coder/JASM/commit/ef04c012786ddceb3b85afb435f963a702d60ad4))
* **input:** 注册 GetAsyncKeyState ([09ff37a](https://github.com/qsy123-coder/JASM/commit/09ff37ad69a8880adfdb0c8580685143a9804143))
* **keyinput:** NativeMethods 补 GetClassName（窗口诊断要读类名） ([69e890f](https://github.com/qsy123-coder/JASM/commit/69e890f5463fb616ef8f372810cad5936e5c5a4a))
* **overlay:** 新增浮窗层级现场探针，用于区分置顶带被压与被合成器绕开 ([2022789](https://github.com/qsy123-coder/JASM/commit/202278932bd25543d09b9bf011a344e9422762b9))
* **overlay:** 注册 GetTopWindow / GetWindow，供真 Z 序链遍历 ([09b996b](https://github.com/qsy123-coder/JASM/commit/09b996be6de3dfe41167c94bfe645e81dfec218b))
* **overlay:** 注册 WindowFromPoint / GetGUIThreadInfo / GetClipCursor ([09f2c3a](https://github.com/qsy123-coder/JASM/commit/09f2c3a4f214c381a14fa5cdd303f2e3404901c1))
* **winui:** NativeMethods.txt 补 SetCursorPos ([e314279](https://github.com/qsy123-coder/JASM/commit/e3142790b52b257d981a3bd73dd3f3eb0048950a))


### Documentation

* **overlay:** 把单选刷新那行的注释改成代码的实际行为 ([0e2c2db](https://github.com/qsy123-coder/JASM/commit/0e2c2db27f5dc1a8a4545dc54edfc66175ce6c4d))
* **overlay:** 探针里那句「多半是完整性级别挡的」猜错了原因 ([fd2760b](https://github.com/qsy123-coder/JASM/commit/fd2760b6c0baae490cb44e0cdb26708236180d7b))
* **overlay:** 订正「前台=我」的读法，实测它会自发出现 ([846323e](https://github.com/qsy123-coder/JASM/commit/846323e6a8895f13dc98b45ba1d91d0a1e661019))
* 第 10 节补助手命令 4（归还前台）与 4.0.0.0 版本门控 ([f9604a7](https://github.com/qsy123-coder/JASM/commit/f9604a74f2128da71c16524d0ffd9d16bf95213b))


### Tests

* **core:** CursorRecenterGuard 单测（没动不还原 / 贴着中心才还原 / 用户自己挪走一律放过） ([ab0fe20](https://github.com/qsy123-coder/JASM/commit/ab0fe20ff9b86a7211446eab44a65f604c66eddc))
* **core:** RefreshSendPacer 单测（首发送键不等待 / 等满阈值 / 边界 / 补发被推后） ([6735179](https://github.com/qsy123-coder/JASM/commit/67351796b5a78acc5771a2c4ca5e2ef3b166ca71))
* **core:** 补归还前台命令 4 的协议测试 ([6fd7279](https://github.com/qsy123-coder/JASM/commit/6fd72791da31f0ca2e35fcdae89fadfd9aff26a0))


### Code Refactoring

* **core:** 窗口句柄行的编解码抽成一份，并加「前台没交还」原因 token ([53e08b2](https://github.com/qsy123-coder/JASM/commit/53e08b2cbdef12547e9ea385b526b931681f87c3))

## [2.28.0](https://github.com/qsy123-coder/JASM/compare/v2.27.1...v2.28.0) (2026-09-24)


### Features

* **build:** 单文件模式也构建助手，产物缺失即失败 ([4ff38c5](https://github.com/qsy123-coder/JASM/commit/4ff38c56cf20bc1f91145e502e8230696e9326b3))
* **core:** 新增 d3dx.ini 定位与目标进程名解析，供提权刷新链路共用 ([d44f5dd](https://github.com/qsy123-coder/JASM/commit/d44f5dd06fad97e807675e7b08fbb25477ff77bb))
* **core:** 新增提权刷新协议：带目标窗口的命令与版本能力判别 ([32c0bec](https://github.com/qsy123-coder/JASM/commit/32c0bec8f6e74ecee5159012b777242e93370dc5))
* **elevator:** ElevatorService 支持请助手代发按键 ([aead020](https://github.com/qsy123-coder/JASM/commit/aead02089e561b7dad1d365b81c23099330517cc))
* **elevator:** 助手路径改用内嵌副本解析，去掉「换 folder 版」的错建议 ([8ee3eea](https://github.com/qsy123-coder/JASM/commit/8ee3eea2158d162029e7018a884ac770dbcd95ca))
* **elevator:** 实现送键命令 3（提权助手代发按键） ([f1d221d](https://github.com/qsy123-coder/JASM/commit/f1d221d6512fde974f15fb1002fd68bb337098a2))
* **elevator:** 把提权助手内嵌进主 exe ([65fb209](https://github.com/qsy123-coder/JASM/commit/65fb209234bb39b1775d29f28d25471355a74d80))
* **elevator:** 新增助手释放与候选选择的纯决策 helper ([031e1c5](https://github.com/qsy123-coder/JASM/commit/031e1c59b6d3d85b167798d8181970a18b1d7256))
* **elevator:** 新增带目标窗口的刷新命令 2，抢不到前台就拒发 F10 并回执原因 ([792cc0b](https://github.com/qsy123-coder/JASM/commit/792cc0b3e88e52e050f5ee7cf569806baca0bf57))
* **elevator:** 新增把内嵌助手释放到 LOCALAPPDATA 的执行体 ([640e136](https://github.com/qsy123-coder/JASM/commit/640e136e0c820632bc248afc6be2f895bbe27611))
* **elevator:** 新增送键命令协议（Elevator 管道命令 3） ([3be4eb4](https://github.com/qsy123-coder/JASM/commit/3be4eb4ebe7dbea6b76d6005a38560767ba2219a))
* **elevator:** 标记 FileVersion 2.0.0.0 作为「认识 2 命令」的能力标记 ([62f7d4c](https://github.com/qsy123-coder/JASM/commit/62f7d4cd7111f4971d4b4cc264339b3109d1433a))
* **elevator:** 注册 ElevatorProvisioner 单例 ([e1e856b](https://github.com/qsy123-coder/JASM/commit/e1e856bb3e42980f12cfc0a1c9cd7abc827aa394))
* **elevator:** 能力标记抬到 3.0.0.0 并链入按键协议源文件 ([9798835](https://github.com/qsy123-coder/JASM/commit/9798835d7f270c7bc06eac6c074179e4579cf496))
* **i18n:** 补浮窗标题与搜索框占位文案（en-us） ([cb65f68](https://github.com/qsy123-coder/JASM/commit/cb65f68e3a147b13193426dad67cd59ea1f2014f))
* **i18n:** 补浮窗标题与搜索框占位文案（es-ar） ([9077fad](https://github.com/qsy123-coder/JASM/commit/9077fad96989bcdf59cefb5be6e014e036d2d96b))
* **i18n:** 补浮窗标题与搜索框占位文案（ru-ru） ([51c130b](https://github.com/qsy123-coder/JASM/commit/51c130bf632b28aae66a566387be7498eef8aa74))
* **i18n:** 补浮窗标题与搜索框占位文案（zh-cn） ([a56973f](https://github.com/qsy123-coder/JASM/commit/a56973fc476fdc3ffcc4fda0289e820e37732ca4))
* **keybinding:** KeySwap 面板增加段名中文显示属性 ([d1ed1f5](https://github.com/qsy123-coder/JASM/commit/d1ed1f50318f72b8de195d85b88ebbd99a3f2add))
* **keybinding:** KeySwap 面板段名改绑中文显示 ([97909a0](https://github.com/qsy123-coder/JASM/commit/97909a08373fc498a4c7d9935d23546d457a3996))
* **keybinding:** NativeMethods 补充完整性级别探测所需 Win32 API ([2f89d1e](https://github.com/qsy123-coder/JASM/commit/2f89d1eba98581cadb72ff08a9fb7ed31cd4aa45))
* **keybinding:** 加按键徽章点击发送处理器 ([ec82b53](https://github.com/qsy123-coder/JASM/commit/ec82b53107348e50a9c5f3ba0a00c60d3e0fd169))
* **keybinding:** 发送前比对进程完整性级别，注定被 UIPI 丢掉时前置拒绝 ([36b4b46](https://github.com/qsy123-coder/JASM/commit/36b4b46e4bbd417945e5d30e1b2e7887e9b0baa6))
* **keybinding:** 声明 SendInput / EnumWindows 等 Win32 API ([8226bd4](https://github.com/qsy123-coder/JASM/commit/8226bd40ed3c407a11aae3bbf2dfb82b7868e28d))
* **keybinding:** 实现定位游戏窗口并合成按键发送 ([dbda091](https://github.com/qsy123-coder/JASM/commit/dbda091b5191795b745bebb8e63d0a483e85f60f))
* **keybinding:** 按键徽章改为可点击按钮 ([9039aaf](https://github.com/qsy123-coder/JASM/commit/9039aafa62c8a2872fc28db4c7fc24cbf73e3e06))
* **keybinding:** 按键映射标签输出中文 ([eabba7f](https://github.com/qsy123-coder/JASM/commit/eabba7f8bac8064dcde5809f480eb14dfb63deed))
* **keybinding:** 新增 d3dx.ini target 解析 ([1c6100c](https://github.com/qsy123-coder/JASM/commit/1c6100c61f30ca5d431f5d6b8da3accaeea083b0))
* **keybinding:** 新增 IGameKeySender 接口与发送状态 ([9498e61](https://github.com/qsy123-coder/JASM/commit/9498e6115d7ace99235f5b9a3bf3016fc355f864))
* **keybinding:** 新增 NeedsElevation 状态，区分 UIPI 拦截与 SendInput 被拒 ([8ab73ea](https://github.com/qsy123-coder/JASM/commit/8ab73eaaf30b0f6f4547df1a32a18dca966f101c))
* **keybinding:** 新增唯一的 SendInput 实现，主程序与助手共用 ([af1c875](https://github.com/qsy123-coder/JASM/commit/af1c8751c5b4048ac0c37fe8a15f57451e5112a2))
* **keybinding:** 新增按键名→显示+VK码词表 ([7df2382](https://github.com/qsy123-coder/JASM/commit/7df23823a9d619292df56eaf367f7de304b23fa5))
* **keybinding:** 新增按键标签汉化词表与本地化器 ([4054beb](https://github.com/qsy123-coder/JASM/commit/4054beb01668117a7426e156636136d754706cc6))
* **keybinding:** 新增提权助手的行协议，载荷全为数字以防注入 ([c77ca30](https://github.com/qsy123-coder/JASM/commit/c77ca3083f9b10578bfe4bf5cfd844015b008666))
* **keybinding:** 新增窗口/进程只读查询，供提权助手校验目标窗口 ([f96a699](https://github.com/qsy123-coder/JASM/commit/f96a699f868e8e571e3baf68a58d3bd1a8f63ec3))
* **keybinding:** 解析出 VK 码与修饰键并带到 UI 模型 ([3c1838b](https://github.com/qsy123-coder/JASM/commit/3c1838b02e9e96ac943745824e622ffa9c5845fa))
* **keybinding:** 需提权状态给出「以管理员身份重开 JASM」的中文提示 ([7db6ea0](https://github.com/qsy123-coder/JASM/commit/7db6ea077319b27227d3d7d1240a98d2157d9995))
* **localize:** 按键映射段名补 250+ 词条，实测 247 个真实段名的完整汉化率 144→212 ([89959c0](https://github.com/qsy123-coder/JASM/commit/89959c06d6638277981807c87b2337a3071aa218))
* **market:** ModMarketMod 实现 IMarketModRow ([85f1e54](https://github.com/qsy123-coder/JASM/commit/85f1e544b308e29c2083ef169c68bc1e0520ae17))
* **market:** 复刻 PostgREST ilike 的 LIKE 模式匹配 ([97fc745](https://github.com/qsy123-coder/JASM/commit/97fc74529806e781c701f7748cc21960fd42dcd0))
* **market:** 失败态可见化并加降级横幅 ([1935cd8](https://github.com/qsy123-coder/JASM/commit/1935cd861a29379389cbbae4f0a4d6785fb9fa78))
* **market:** 市场查询失败时降级到 COS 快照 ([e671f5c](https://github.com/qsy123-coder/JASM/commit/e671f5c341bac4309c7f319ea1c4777e7b87a2be))
* **market:** 抽出共用的 JSON 反序列化选项 ([2fa8b3c](https://github.com/qsy123-coder/JASM/commit/2fa8b3cc2c9d51adc31012848624990b4aaa18cf))
* **market:** 新增 IMarketModRow 最小行视图接口 ([b0d5697](https://github.com/qsy123-coder/JASM/commit/b0d5697e1e8b8bf260a466f78963c31918fbf12a))
* **market:** 新增 MarketQuery 查询模型与分页结果 ([6b977c3](https://github.com/qsy123-coder/JASM/commit/6b977c3fcfcee97bf08cdd806eb321dfe0027508))
* **market:** 新增分类计数唯一收敛点 MarketCategoryTally ([6d532f3](https://github.com/qsy123-coder/JASM/commit/6d532f36e5aea7dbc4bd95113f7dbb4a341463a5))
* **market:** 新增快照下载/解压/缓存服务 ([fafaa11](https://github.com/qsy123-coder/JASM/commit/fafaa111f5e38c097300ba6d38938c869ea8f048))
* **market:** 新增快照载体,持有 JsonDocument 不释放 ([4b4c206](https://github.com/qsy123-coder/JASM/commit/4b4c2060fe8d0d3a89950174f1afe441193df395))
* **market:** 新增本地筛选/排序/分页引擎 ([df71e2e](https://github.com/qsy123-coder/JASM/commit/df71e2e2da8ca4ae64fe68e4ccb14a360f505a5b))
* **market:** 空状态文案绑定与重试按钮 ([cc303c6](https://github.com/qsy123-coder/JASM/commit/cc303c6dea1e420a0e6f873b2126626790b55197))
* **market:** 结果模型增加降级标记与错误信息 ([39fede6](https://github.com/qsy123-coder/JASM/commit/39fede69e12c485527c4550cbe840ab5dce3ddc8))
* **market:** 配置项新增快照地址 SnapshotUrl ([ed295fd](https://github.com/qsy123-coder/JASM/commit/ed295fd15abd87432c8bb54ca4c960f3fdab19e1))
* **overlay:** Mod 行加上勾选命令与失败回滚 ([22caa3d](https://github.com/qsy123-coder/JASM/commit/22caa3d65bfe3d93d2f85fbbcbe48263540d2729))
* **overlay:** NativeMethods 补浮窗热键与扩展样式读写所需 API ([7575372](https://github.com/qsy123-coder/JASM/commit/7575372e901353d40f88b0112b34a3ab3004629a))
* **overlay:** 刷新协调器给出状态行用的成功/失败标志 ([1055c9c](https://github.com/qsy123-coder/JASM/commit/1055c9c29b8ab8d5a0ad554850700d1f00c77b0b))
* **overlay:** 协调器暴露 CanRequestRefresh 供刷新按钮置灰 ([0e3014a](https://github.com/qsy123-coder/JASM/commit/0e3014a438c53994ec9e22b293810cc88e850eeb))
* **overlay:** 启动时拉起浮窗（当前仅鸣潮） ([9275098](https://github.com/qsy123-coder/JASM/commit/927509850ce51d1db0eaca79f7297a5739f69f4d))
* **overlay:** 新增刷新合并状态机 ([66adafa](https://github.com/qsy123-coder/JASM/commit/66adafa51c73f9be04752e8732f9211b68923264))
* **overlay:** 新增浮窗 Mod 列表的过滤与排序 ([b506f4a](https://github.com/qsy123-coder/JASM/commit/b506f4a4c5a16de449fdbfe110e4b1ea92e33790))
* **overlay:** 新增浮窗 Mod 行模型（字段映射对齐主窗口画廊） ([a87ff49](https://github.com/qsy123-coder/JASM/commit/a87ff49e065ec6ad64ca9f1525826d4aad689abc))
* **overlay:** 新增浮窗 ViewModel（角色/搜索/勾选即刷新/双向同步） ([4168036](https://github.com/qsy123-coder/JASM/commit/416803689f56a866eb7aeaac176daf21cdd9f1fa))
* **overlay:** 新增浮窗位置解析（居中 + 夹回工作区） ([515a8ab](https://github.com/qsy123-coder/JASM/commit/515a8abfb767c088aba502822098dc2f62801437))
* **overlay:** 新增浮窗候选热键表与挑选规则 ([21d570d](https://github.com/qsy123-coder/JASM/commit/21d570d11d5b708f928d19efdd5a1defb3166ad7))
* **overlay:** 新增浮窗全局热键注册服务 ([4f70bd9](https://github.com/qsy123-coder/JASM/commit/4f70bd9998da6d6111e66eb8a6b7ad2aa07e010d))
* **overlay:** 新增浮窗刷新协调器（合并 + 结局上报） ([542a186](https://github.com/qsy123-coder/JASM/commit/542a186fd37bf70a76e037e2d231e9bc7eba5dce))
* **overlay:** 新增浮窗刷新结局的分类与文案 ([56f00d3](https://github.com/qsy123-coder/JASM/commit/56f00d3a394ecc80bc17ce47f6300518058f56db))
* **overlay:** 新增浮窗宿主服务（建窗口 + 全局热键 + 按热键切显隐） ([92c2433](https://github.com/qsy123-coder/JASM/commit/92c24332692eac2b918bed24e325404f8bee4cfb))
* **overlay:** 新增浮窗界面（角色选择/搜索/状态行/勾选列表 + 拖动条） ([3c16de9](https://github.com/qsy123-coder/JASM/commit/3c16de9ca8b66e997df8c1fdfd578156d1cc7c47))
* **overlay:** 新增浮窗窗口本体（无边框置顶不夺焦点 + 拖动/位置记忆） ([a7fc404](https://github.com/qsy123-coder/JASM/commit/a7fc4045f82356b37761da198014a51b35d2f475))
* **overlay:** 新增浮窗设置（窗口座标 + 上次选中角色） ([fc78fcb](https://github.com/qsy123-coder/JASM/commit/fc78fcb3913a171720dd5ce10bcf89cca3b414dd))
* **overlay:** 注册浮窗刷新协调器与宿主服务 ([0ff2ac6](https://github.com/qsy123-coder/JASM/commit/0ff2ac644ca01f709b3af8c3d858a3cc54378ecb))
* **overlay:** 浮窗 ViewModel 暴露列表是否为空，供空态提示控制显隐 ([3a2458a](https://github.com/qsy123-coder/JASM/commit/3a2458a704d678b62e173fad3bbff3d2da076c4e))
* **overlay:** 浮窗刷新结局补「助手版本过旧」 ([e2cf30b](https://github.com/qsy123-coder/JASM/commit/e2cf30beab12c0150c8f2d5cb3c306c2048d1bb8))
* **overlay:** 浮窗支持单选/多选模式，单选独占并自动刷新 ([8a0238d](https://github.com/qsy123-coder/JASM/commit/8a0238dad66f6ec64ce20a3d8ba8a574b8a150c9))
* **overlay:** 浮窗新增模式选择与手动刷新按钮 ([f3d844f](https://github.com/qsy123-coder/JASM/commit/f3d844f9b487e45f671f89db308233e04199db27))
* **overlay:** 移植浮窗扩展样式读写服务 ([c8f7169](https://github.com/qsy123-coder/JASM/commit/c8f7169d8778ce7d94a8bf89565e8588bcbfa93b))
* **overlay:** 补充单选/多选/刷新的中文文案 ([4a064ca](https://github.com/qsy123-coder/JASM/commit/4a064ca856b53a20194994b9b2d32145e0815f35))
* **overlay:** 补充单选/多选/刷新的俄语文案 ([40c5113](https://github.com/qsy123-coder/JASM/commit/40c5113f0b3e1e5759a5d947b66db305c148a006))
* **overlay:** 补充单选/多选/刷新的英文文案 ([157e978](https://github.com/qsy123-coder/JASM/commit/157e978892831a67a8a3ccb8c2a99449feadbc1d))
* **overlay:** 补充单选/多选/刷新的西语文案 ([486cb48](https://github.com/qsy123-coder/JASM/commit/486cb485a4082e8fa715755dc0c78d1d163be4ed))
* **overlay:** 角色下拉显示头像（DisplayMemberPath 换成 ItemTemplate） ([b564678](https://github.com/qsy123-coder/JASM/commit/b5646786d08478111ed8d6c20748a1670d94ca13))
* **overlay:** 角色下拉行改为「角色 + 头像」的显示包装 ([263857b](https://github.com/qsy123-coder/JASM/commit/263857bed76818c48cf94c88532e64944ff8d588))
* **overlay:** 角色列表改用带头像的 OverlayCharacterItem ([b8a87de](https://github.com/qsy123-coder/JASM/commit/b8a87de7e3a7f94c448930aa2d1f46039a1b2794))
* **overlay:** 设置新增 MultiSelectMode 开关(默认单选) ([8332675](https://github.com/qsy123-coder/JASM/commit/8332675b6a18b4647d217a8f8071286584b37c92))
* **spike:** 原型启动逻辑，未处理异常一律落盘 ([2da40fb](https://github.com/qsy123-coder/JASM/commit/2da40fbb363cc6df33b5f04310cee1628cb5ed40))
* **spike:** 新增全局热键注册，候选键列表自动挑第一个可用的 ([fc4f82a](https://github.com/qsy123-coder/JASM/commit/fc4f82a13909b0a910ac5c08c58330ac3fe82850))
* **spike:** 新增原型 App 入口 XAML ([724de5e](https://github.com/qsy123-coder/JASM/commit/724de5e4c1c85605dd2dcefadbaa9838935ae22c))
* **spike:** 新增原型窗口逻辑，含置顶自愈、点击计数、热键显隐、拖动 ([4729214](https://github.com/qsy123-coder/JASM/commit/4729214c5029f0888d145947f7ee70232a04ef16))
* **spike:** 新增扩展样式读写层，NOACTIVATE/TOPMOST 一律以回读为准 ([82bf9ff](https://github.com/qsy123-coder/JASM/commit/82bf9ff7ab8890e7a4a348611e3c87ee0d677d12))
* **spike:** 新增落盘日志 SpikeLog，窗口自己消失时留下证据 ([deb2b71](https://github.com/qsy123-coder/JASM/commit/deb2b7137ea05a170a9b219b80921510d0b99880))
* **spike:** 新增诊断面板 XAML，按钮配色不依赖主题笔刷 ([439ba40](https://github.com/qsy123-coder/JASM/commit/439ba403ede65e13c74ece3c282aaa3cb796a547))
* **spike:** 新增鸣潮游戏内浮窗 Phase 0 原型工程，刻意不进 sln ([db5555c](https://github.com/qsy123-coder/JASM/commit/db5555c4598cb1126e677e3d1ba7e0727e2ab4d9))
* **winui:** 刷新按 Elevator 版本分流，带目标窗口并处理回执 ([50de71c](https://github.com/qsy123-coder/JASM/commit/50de71cf5527d682e6e6f699f007fc8b2bddd2ca))


### Bug Fixes

* **build:** 打包前清掉 output/ 与同名旧压缩包 ([dee9981](https://github.com/qsy123-coder/JASM/commit/dee998120e0a27d5b56b980ab23de528415cede9))
* **build:** 提交单文件发布配置 FolderProfileSingleFile.pubxml（CI 缺它只能产出空壳 exe） ([b3f4a75](https://github.com/qsy123-coder/JASM/commit/b3f4a75b6fb0cc3581ef5b1fafb326b7e85296da))
* F10 提权刷新同样先由 JASM 同步切前台 ([0a4418f](https://github.com/qsy123-coder/JASM/commit/0a4418fa2aaadf1770793d81e4e85c4378fcf0c8))
* **ini:** GetIniValue 只按首个等号切分，不再把值里的 = 吞掉 ([14062d8](https://github.com/qsy123-coder/JASM/commit/14062d8bf71d93c4ca41b06fd357910c8f3b50a4))
* **keybinding:** 按键名裸写(. / Home / PgUp)解析不到键码，徽章灰掉点不了 ([1249feb](https://github.com/qsy123-coder/JASM/commit/1249feb2473826264e59b6f814bffbdf9826cc7b))
* **keyinput:** NeedsElevation 兜底文案不再劝用户换 folder 版 ([abc6dd0](https://github.com/qsy123-coder/JASM/commit/abc6dd0900f99f9acf6a6788cfaebe74467edb93))
* **keyinput:** 游戏提权时改由提权助手代发按键，不再让用户重启 JASM ([a46a2aa](https://github.com/qsy123-coder/JASM/commit/a46a2aae2d95b816d45933045e77469261ff4ffd))
* **keyinput:** 送键失败文案区分「助手不可用」与「助手拒发」 ([6b6d88a](https://github.com/qsy123-coder/JASM/commit/6b6d88a54da2090745091c4d3ce50eb457e3dfcb))
* not-foreground 文案不再建议「先点一下游戏画面再试」 ([ab1bb36](https://github.com/qsy123-coder/JASM/commit/ab1bb368b294a5860e3ba51431e7c65a42edc1d8))
* **overlay:** 勾选即刷新改走按键徽章的送键通道，修复游戏内勾选不重载 ([d57ed6e](https://github.com/qsy123-coder/JASM/commit/d57ed6e1447bb9339aa2dfead63093c36a546b16))
* **overlay:** 建窗口前先切回 UI 线程（线程池上 new Window 会以 stowed exception 打挂进程） ([a3e1026](https://github.com/qsy123-coder/JASM/commit/a3e102649a93d3adfb49978ccab442acd47d4102))
* **overlay:** 手动初始化页面级 x:Bind（本窗口永不激活，生成的 Activated 钩子从不触发） ([681adb2](https://github.com/qsy123-coder/JASM/commit/681adb2d48dc30466b93ecf89a787ca1190ad66e))
* **overlay:** 改为首次显示时摆位并确认置顶（浮窗从不 Activate，Activated 事件不会来） ([367290c](https://github.com/qsy123-coder/JASM/commit/367290c131506218e1cb237ae81d5006303decf5))
* **overlay:** 浮窗文字看不清（钉死 Dark 主题 + 提高对比）与状态文案被右边缘切掉 ([b72c527](https://github.com/qsy123-coder/JASM/commit/b72c5275eaa9640fef95cd709aee0506166da038))
* 新增 ForegroundWindowActivator 统一「还原并切前台」 ([5e13e3e](https://github.com/qsy123-coder/JASM/commit/5e13e3e36c5d77c945ca1aec62f7ce59a29e55d4))
* 送键前由 JASM 在同步段切前台，不再指望提权助手抢 ([e42fe87](https://github.com/qsy123-coder/JASM/commit/e42fe87128bb9c6306da8838a405cadc286a0a71))


### Performance Improvements

* **overlay:** 拖动改用「按下时光标 + 窗口位置」算绝对坐标 ([718fa5d](https://github.com/qsy123-coder/JASM/commit/718fa5d371c7cb1249a21eaef4345a33c976a2b0))


### Miscellaneous

* .gitignore 忽略本地构建/发布产物与截图，消除 IDE 的 897 项改动噪音 ([ab71ac1](https://github.com/qsy123-coder/JASM/commit/ab71ac17e35ad1b91b7474fa9ae8231661f3cfb0))
* **config:** appsettings 补上 COS 快照地址 ([50739b5](https://github.com/qsy123-coder/JASM/commit/50739b54d18ecd5d54c2c68a860df2cb5e0973d8))
* **di:** 注册 IGameKeySender ([91f50bb](https://github.com/qsy123-coder/JASM/commit/91f50bb1c392e80ba7e200b3bb2db88b4bd6cce1))
* **di:** 注册独立的快照 HttpClient 与服务 ([1a553e4](https://github.com/qsy123-coder/JASM/commit/1a553e4b5cfd76e29a1ecd45d581b7576291ac7b))
* **elevator:** 刷新回执超时的日志文案同步去掉「完整包」 ([a695bfc](https://github.com/qsy123-coder/JASM/commit/a695bfc01d23b56772a53405baf021738e59eb62))
* **gitignore:** 反忽略 Properties/PublishProfiles/*.pubxml，发布配置必须进版本库 ([1ae8890](https://github.com/qsy123-coder/JASM/commit/1ae8890f2b02eec6ac726a3936025c3f71fdb420))
* **keybinding:** CsWin32 清单补 QueryFullProcessImageName ([a30f531](https://github.com/qsy123-coder/JASM/commit/a30f53103bb31b5661528c5868e12dddc52453ae))
* NativeMethods.txt 补 AllowSetForegroundWindow（切前台授权） ([e34a06f](https://github.com/qsy123-coder/JASM/commit/e34a06fc63bd601f34c6228fe4832a7faf00006a))
* **overlay:** NativeMethods 补 GetCursorPos / GetWindowRect（拖动要读光标与窗口真实位置） ([aa343ef](https://github.com/qsy123-coder/JASM/commit/aa343ef5c2f8feeb634bbad7ef901d71e9143a22))
* **spike:** CsWin32 源生成清单，声明原型用到的 win32 函数 ([5dee2ea](https://github.com/qsy123-coder/JASM/commit/5dee2ea65e9a3007315f830320efed306253a5c5))
* **spike:** 清单声明 asInvoker —— 要验的正是非提权进程能否盖在提权游戏上 ([6772aaa](https://github.com/qsy123-coder/JASM/commit/6772aaad76ac8045dc16116f1857d7eed40edec5))


### Documentation

* **overlay:** 归档鸣潮浮窗 Phase 1 实机验收结果，标注 §17.4 阻塞项已解 ([0fd895b](https://github.com/qsy123-coder/JASM/commit/0fd895bf40a76bead65366f005c8a97662801bd1))
* README 订正「勾选 Mod 会自动刷新」与助手指令的过期描述 ([19af929](https://github.com/qsy123-coder/JASM/commit/19af9292c346a7fea4c3fbb0285d560ebe240cb3))
* **spike:** 记录 Phase 0 的四个问题、三个坑与 Go 判定 ([9fbb24d](https://github.com/qsy123-coder/JASM/commit/9fbb24d2c460571805555b3b9ae61e8281c8d88f))
* 修正发布链路里已过期的描述，补「助手内嵌」与单文件 workflow ([1d34461](https://github.com/qsy123-coder/JASM/commit/1d3446146c3a4e77c62b068f4076c3eede7834b6))
* 新增第 17 节，归档游戏内浮窗 Phase 0 实测基线 ([99670cc](https://github.com/qsy123-coder/JASM/commit/99670ccdcda175bf405929e23a50ff865c6ecdf9))
* 新增鸣潮游戏内浮窗切换 Mod 的 PRD ([f873bdd](https://github.com/qsy123-coder/JASM/commit/f873bddfa4d9f6a5fd13a255d11ab8f724e96304))


### Continuous Integration

* folder 包改为构建并打包 Elevator.exe，修好助手发不出去的链路 ([b950fd5](https://github.com/qsy123-coder/JASM/commit/b950fd580d5fdfa956cf7baafa52d6b6305c6840))
* 单文件包的 workflow 补上 MSVC 探测 ([4811115](https://github.com/qsy123-coder/JASM/commit/4811115710470cb2662e86ed0d9f1881a5dd673e))
* 新增单文件包构建 workflow ([3516c11](https://github.com/qsy123-coder/JASM/commit/3516c11020414f5ac5b716c5e1c76035522bdcfa))
* 绕开 ILCompiler 过期的 VS 版本探测，修好 AOT 助手构建 ([7af2664](https://github.com/qsy123-coder/JASM/commit/7af2664fb7b90f4a31645480a1b3b06eb742553d))
* 自包含包同样构建并打包 Elevator.exe ([416fe4d](https://github.com/qsy123-coder/JASM/commit/416fe4df0425e8baf2972d91d5c9f98bd361e5e9))
* 自包含包同样绕开 ILCompiler 过期的 VS 版本探测 ([993bc73](https://github.com/qsy123-coder/JASM/commit/993bc735baa1f5990a17ce84214967ca54b4aba8))


### Tests

* **core:** 覆盖 d3dx.ini 候选顺序、空路径守卫与目标解析 ([18dbadf](https://github.com/qsy123-coder/JASM/commit/18dbadfccd00f04cc094321887722813527360bb))
* **core:** 覆盖刷新命令字面量、版本门与回执解析 ([e331d73](https://github.com/qsy123-coder/JASM/commit/e331d73754656a0722681dca812777ee05d4ca11))
* **elevator:** 覆盖助手释放门与候选选择的边界 ([a104312](https://github.com/qsy123-coder/JASM/commit/a1043125e8c00e37844207be89c03dbaf3bbff5c))
* **elevator:** 覆盖送键协议的命令字、版本门与载荷往返 ([20a11fe](https://github.com/qsy123-coder/JASM/commit/20a11feeef675eb3c2fd97b2deb80d3b413fbd7b))
* **keybinding:** 补充按键标签汉化单元测试 ([4a505c3](https://github.com/qsy123-coder/JASM/commit/4a505c340df35a3ee5197a8873224dcafc62a75a))
* **keybinding:** 覆盖 d3dx.ini target 解析 ([0ba433a](https://github.com/qsy123-coder/JASM/commit/0ba433a18aa364c9ce1edaf24c05c21a438bc977))
* **keybinding:** 覆盖护栏的修饰键白名单、重复修饰键与危险组合 ([9d613ba](https://github.com/qsy123-coder/JASM/commit/9d613ba92cbab2bcd25885c094893cba06a02275))
* **keybinding:** 覆盖按键可发送判定 ([c470683](https://github.com/qsy123-coder/JASM/commit/c470683742c3cc7ffb04d55d1d14e6e219fb6e38))
* **keybinding:** 覆盖按键名显示与 VK 码映射 ([b137822](https://github.com/qsy123-coder/JASM/commit/b13782275648ef31cc9ea7f22d05e08330d6b065))
* **keybinding:** 覆盖行协议的编解码与各类非法载荷 ([017ee6a](https://github.com/qsy123-coder/JASM/commit/017ee6a96c88c9ed7a0f047ee0b77f47466feaf7))
* **keybinding:** 覆盖裸显示名别名与补齐的 F13-24 / 小键盘运算符 / 锁定键 ([e4133e6](https://github.com/qsy123-coder/JASM/commit/e4133e616edb4f438275c035e30be3e1067be548))
* **keybinding:** 覆盖裸符号键、含等号的 = 键、鼠标键不可发送 ([3b401ea](https://github.com/qsy123-coder/JASM/commit/3b401eabea1482b996a23ca92381203b9b7ec743))
* **localize:** 钉住实测段落名的汉化结果，含刻意不翻的不透明作者 ID ([93ac7a5](https://github.com/qsy123-coder/JASM/commit/93ac7a551e4b0c12b0a33c09bf36a8a487bc6cf2))
* **market:** JsonElement 适配器作为 IMarketModRow 测试实现 ([d072f45](https://github.com/qsy123-coder/JASM/commit/d072f459fd061605c550b17ccd30c9c7c3f24190))
* **market:** 快照 fixture 随构建产物落盘 ([6da25b0](https://github.com/qsy123-coder/JASM/commit/6da25b0f7c10d3f0fe59b253de6332c2336f8fc1))
* **market:** 新增快照 fixture(边界样本) ([3cd840c](https://github.com/qsy123-coder/JASM/commit/3cd840cea6cbaeeeeac45e24090a8ce74c5d5e83))
* **market:** 覆盖 ilike 通配符语义 ([74f9b51](https://github.com/qsy123-coder/JASM/commit/74f9b516e3329dcca883746399b11de9ed1d058d))
* **market:** 覆盖分类计数算术与两路一致性 ([a276362](https://github.com/qsy123-coder/JASM/commit/a2763625b8bdfec48cbdc897cf54ca4b89c9444d))
* **market:** 覆盖快照解压与容错反序列化 ([617d72f](https://github.com/qsy123-coder/JASM/commit/617d72f5567717d7f65b6cf7bfec15ce6f800e52))
* **market:** 覆盖查询引擎筛选/排序/分页 ([b13e576](https://github.com/qsy123-coder/JASM/commit/b13e5766087167f36c60541646df64a727c78c56))
* **overlay:** 刷新结局文案用例改跟新词表 ([e956436](https://github.com/qsy123-coder/JASM/commit/e9564369dc5b586ac3c6d3669241437ed73d2c0a))
* **overlay:** 覆盖「助手版本过旧」的文案方向 ([025ee02](https://github.com/qsy123-coder/JASM/commit/025ee02e91b4ed57721fe10fbbe5eaf243b1dc55))
* **overlay:** 覆盖候选热键的挑选、回退与失败文案 ([e74c550](https://github.com/qsy123-coder/JASM/commit/e74c55005f36d50043abc7646f732daf9431572a))
* **overlay:** 覆盖刷新合并的补发与在跑状态 ([821b9c8](https://github.com/qsy123-coder/JASM/commit/821b9c8e883c6180e0e8ef9f0d3595ea02185cb7))
* **overlay:** 覆盖刷新结局的归类与文案 ([17bbfd7](https://github.com/qsy123-coder/JASM/commit/17bbfd7544d7532302c72d29fe26d255b4515451))
* **overlay:** 覆盖浮窗位置的居中、夹取与负坐标 ([099e708](https://github.com/qsy123-coder/JASM/commit/099e7085dc3cfb0011cc0967cbaa950f6432f3a6))
* **overlay:** 覆盖浮窗过滤的匹配字段与已启用前移 ([8d1d652](https://github.com/qsy123-coder/JASM/commit/8d1d652695cd1ba1e3468985bcf897475cf16364))


### Code Refactoring

* **elevator:** 删掉已被送键通道取代的浮窗刷新方法 ([17971c3](https://github.com/qsy123-coder/JASM/commit/17971c30ef46914488459c7bbd461c8c650115c2))
* **elevator:** 带目标的刷新把回执返回给调用方，并新增浮窗刷新入口 ([dfecffc](https://github.com/qsy123-coder/JASM/commit/dfecffc39f0e5bfb77ae40a36f02e8e82789698a))
* **keybinding:** 把危险组合键护栏抽到 Core，主程序与提权助手共用同一份实现 ([05c2745](https://github.com/qsy123-coder/JASM/commit/05c2745ae00181affd20d3d7347019f086567f37))
* **keybinding:** 改用 Core 的 KeyChordGuard，不再自带一份护栏 ([17be2f6](https://github.com/qsy123-coder/JASM/commit/17be2f6b6baaecbcff0566fb8a7656d85b984bed))
* **keyinput:** 送键结果带上动态失败原因 ([6bed648](https://github.com/qsy123-coder/JASM/commit/6bed648c8d44c2611de19ee82cb3162eed47dd31))
* **overlay:** 刷新结局词表改跟送键通道，删掉助手回执那一套 ([b9750a0](https://github.com/qsy123-coder/JASM/commit/b9750a0b6ca4ba2c52ad006a5d1d3844512bd0e8))
* **winui:** GameKeySender 复用 D3dxIniTargetResolver 与 WindowProcessQuery ([832cda4](https://github.com/qsy123-coder/JASM/commit/832cda40808d921544f090d02f8ae651deb721e4))
* **winui:** GetProcessIds/FindGameWindow 移入 WindowProcessQuery，行尾规整为 CRLF ([bed7b5c](https://github.com/qsy123-coder/JASM/commit/bed7b5cd415a65bde1141fe5e15e7c73314e9d05))

## [2.27.1](https://github.com/qsy123-coder/JASM/compare/v2.27.0...v2.27.1) (2026-09-14)


### Bug Fixes

* **logging:** 日志路径改为绝对路径，不再依赖进程工作目录 ([7744636](https://github.com/qsy123-coder/JASM/commit/774463616ec1cc0bf628d98ef5e690b8e8abfcf8))
* **update:** 自更新重启时带上工作目录，避免日志落到临时目录 ([99a18e8](https://github.com/qsy123-coder/JASM/commit/99a18e844b4d0174da3ca4f3481c0284b693b508))

## [2.27.0](https://github.com/qsy123-coder/JASM/compare/v2.26.1...v2.27.0) (2026-09-14)


### Reverts

* **startup:** 撤掉启动页的 XXMI 目录选择器 ([d3300f3](https://github.com/qsy123-coder/JASM/commit/d3300f368d81aa3bae891d45be06a72f6691767a))
* **startup:** 撤掉启动页的 XXMI 路径行，改放到向导「安装位置」行 ([47ee6db](https://github.com/qsy123-coder/JASM/commit/47ee6db428bb60abaa32271cef2683458939269c))


### Features

* **build:** 新增 XXMI 多版本包打包脚本 PackXxmiVersions.py ([693711c](https://github.com/qsy123-coder/JASM/commit/693711cd2a3e3f62c07e95929eb64f7cb55cf211))
* **modenv:** DI 注册版本目录服务与备份服务 ([13da6c9](https://github.com/qsy123-coder/JASM/commit/13da6c90b10d9d9c11c21cc92039ee7fbbdb7f94))
* **modenv:** Facade 接入版本选择、版本回退与备份还原 ([81a0088](https://github.com/qsy123-coder/JASM/commit/81a0088654b2ddbd694115219e17b2cf44b92d94))
* **modenv:** InstallPackageAsync 支持镜像目标目录 ([c01c6bb](https://github.com/qsy123-coder/JASM/commit/c01c6bb05f9fbb0f100f755feb5972dc0abb7277))
* **modenv:** ModEnvSetupOptions 增加 VersionCatalogUrl 与 KeepBackupCount ([b38c93d](https://github.com/qsy123-coder/JASM/commit/b38c93d97b34afe18815efb95aff536f87891a00))
* **modenv:** 一键配置支持调用方指定 XXMI 根目录 ([c7c23dd](https://github.com/qsy123-coder/JASM/commit/c7c23dd2ad112f76829e5119cbfab1a700a003a8))
* **modenv:** 向导「安装位置」行右侧加「更改…」「用默认」 ([90aadeb](https://github.com/qsy123-coder/JASM/commit/90aadebece231024df6764cdfac5ad5b6c5fa7b6))
* **modenv:** 向导内选择 XXMI 安装目录并即时重跑预检 ([a80c6fd](https://github.com/qsy123-coder/JASM/commit/a80c6fd782615734898e590bb33ef56fcb192722))
* **modenv:** 向导接受调用方传入的 XXMI 根目录 ([f30a23b](https://github.com/qsy123-coder/JASM/commit/f30a23b5f2e8ace1f827795c8aacd61c612c5dae))
* **modenv:** 向导暴露 HasCustomRootFolder 供「用默认」按钮显隐 ([c69e02e](https://github.com/qsy123-coder/JASM/commit/c69e02edf510de3ac8816c932efd992d47f49b21))
* **modenv:** 对话框接线恢复备份按钮 ([06e5fbd](https://github.com/qsy123-coder/JASM/commit/06e5fbd377b37b26c3730d56221f45b100b11e4c))
* **modenv:** 抑制启动器缓存的过期版本提示 ([2f466f6](https://github.com/qsy123-coder/JASM/commit/2f466f6eed387acd91561511d48dd916eae079ff))
* **modenv:** 新增可选版本目录模型 ModEnvVersionCatalog ([df33c96](https://github.com/qsy123-coder/JASM/commit/df33c96228aa83f12f1d2faf5e47641432774d8f))
* **modenv:** 新增版本号比较工具 ModEnvVersion ([11954ed](https://github.com/qsy123-coder/JASM/commit/11954edfe47a884a8948773ce571eaf1ee163d80))
* **modenv:** 新增版本备份服务 ModEnvBackupService ([764e5be](https://github.com/qsy123-coder/JASM/commit/764e5be23e94793670d9745ee52a5fdc0ca3c409))
* **modenv:** 新增版本目录服务 ModEnvVersionCatalogService ([e077a29](https://github.com/qsy123-coder/JASM/commit/e077a29f6b04cf5045dfd7808317a4dfcfc46b86))
* **modenv:** 配置向导 ViewModel 增加版本选择与备份还原 ([12dbcac](https://github.com/qsy123-coder/JASM/commit/12dbcac0123204b3fabdb657c9627e7885ad2b5e))
* **modenv:** 配置对话框增加 XXMI 版本下拉框与备份恢复区 ([0a2ba2c](https://github.com/qsy123-coder/JASM/commit/0a2ba2cf39b26fc88a8bb72f21a0be3a8efc6e2a))
* **options:** 新增 XxmiRootFolderPath 保存 XXMI 安装位置 ([a295e13](https://github.com/qsy123-coder/JASM/commit/a295e13f012f5f33897c8f0167880f8d9723e770))
* **settings:** 一键配置复用启动页选定的 XXMI 根目录 ([1c3fd36](https://github.com/qsy123-coder/JASM/commit/1c3fd36f17f221eb0bffa08557d4c608b966595d))
* **startup:** 启动页支持自选 XXMI 安装位置 ([badf3ca](https://github.com/qsy123-coder/JASM/commit/badf3ca3f93cc9e15bb661d124a9cff28a14751d))
* **startup:** 启动页新增 XXMI 安装位置一行 ([d05dffd](https://github.com/qsy123-coder/JASM/commit/d05dffd89d7411f9ebc89b539a21fd8b2fafddfb))
* **startup:** 新增 XXMI 安装目录选择器 ([508dfcf](https://github.com/qsy123-coder/JASM/commit/508dfcfacf30bd622d446cce06adbb9bc1b3683a))


### Bug Fixes

* **build:** XXMI 版本包白名单纳入 Manifest.json ([28929c6](https://github.com/qsy123-coder/JASM/commit/28929c6606b614d86f369fde3a6bf516d31df589))
* **modenv:** XXMI 基础包同时写入 Resources\Packages\XXMI ([7061016](https://github.com/qsy123-coder/JASM/commit/706101646ba272349a4a98a1a238b61380795e33))
* **modenv:** 向导按钮行固定底部，选完版本无需下滑 ([a1eed11](https://github.com/qsy123-coder/JASM/commit/a1eed1185187a873ac1b5b0b7c4e81ad4704698a))
* **modenv:** 改用对齐启动器缓存版本的方式消除「更新」误报 ([128f1f3](https://github.com/qsy123-coder/JASM/commit/128f1f34d9eac975696110f4cebc7cca46cf6b41))
* **modenv:** 版本下拉默认选中当前已安装版本 ([b5dc9b7](https://github.com/qsy123-coder/JASM/commit/b5dc9b75b57d4e91f44d7ae4ea377b061e6fdbe7))
* **settings:** 向导改过的 XXMI 安装位置立即落盘 ([b186f0b](https://github.com/qsy123-coder/JASM/commit/b186f0beaff0d4ba37bdd75277fde578b13c5cf8))
* **startup:** 向导关闭后立即落盘 XXMI 安装位置 ([138c5ae](https://github.com/qsy123-coder/JASM/commit/138c5ae1a2f2961f54122f983bb549d5ba1f23b7))


### Miscellaneous

* **modenv:** appsettings 填入 VersionCatalogUrl 与 KeepBackupCount ([a12e52a](https://github.com/qsy123-coder/JASM/commit/a12e52aa2dad54c8ffee56723146da94979f58ce))


### Documentation

* **cdn:** 补充 xxmi-versions.json 的生成与上传说明 ([53f6b2e](https://github.com/qsy123-coder/JASM/commit/53f6b2ef31892684a95a0023a3427d6dfe03e356))
* **mod-env-hand-test:** §14.2 兼容性矩阵补实测结果（四版本 Mod 均正常） ([b31b7c5](https://github.com/qsy123-coder/JASM/commit/b31b7c593de1086bee8a1a5ecfeac4c4f80a0d11))
* **mod-env-hand-test:** §14.3 改为「默认选中已装版本」，补重跑/恢复用例 ([3c127f9](https://github.com/qsy123-coder/JASM/commit/3c127f92322ea6df4584dc9a66d434e75d3413a2))
* **mod-env-hand-test:** §15 改为「缓存对齐到实装版本」并记录 skipped_version 不生效的实测 ([17ec046](https://github.com/qsy123-coder/JASM/commit/17ec04657559b23286f832be0e2e2c1da07f2a49))
* **mod-env-hand-test:** §16 改为向导「安装位置」行，补取消/落盘用例 ([d3b8974](https://github.com/qsy123-coder/JASM/commit/d3b89741d81eb87b78e87b3c6e4bec303d00fca4))
* **mod-env-hand-test:** 补充 §16 启动页自选 XXMI 安装位置 ([84fb8cd](https://github.com/qsy123-coder/JASM/commit/84fb8cdf1b5d275a0cc796e18e0cdea261516c1c))
* **mod-env-hand-test:** 补充启动器更新误报抑制的验收项 ([43650c3](https://github.com/qsy123-coder/JASM/commit/43650c31d461038a5e1a5219288b3322c95c1d03))
* **mod-env:** 手测清单覆盖两处框架副本与启动器版本校验 ([8d828a2](https://github.com/qsy123-coder/JASM/commit/8d828a2d97a67b03f5e4a677f2ee1c99d8167f69))
* **mod-env:** 说明 Manifest.json 分发要求与两份清单哈希须一致 ([b8edc2e](https://github.com/qsy123-coder/JASM/commit/b8edc2ee455a2c337649d254055eef29ff47b760))
* **test:** 补 XXMI 版本选择与回退手测清单（第 14 节） ([75f62b3](https://github.com/qsy123-coder/JASM/commit/75f62b376ec68b32a08e4a5e52a27236e9fbb3ce))
* 新增 XXMI 版本选择与回退 PRD ([af7e82d](https://github.com/qsy123-coder/JASM/commit/af7e82da8eb28a51781f7391932881a931bd3129))


### Code Refactoring

* **modenv:** CopyToTargetAsync 由 private 放宽为 internal ([0ae7950](https://github.com/qsy123-coder/JASM/commit/0ae795048a1abcb6fe118188087a0aa15905212d))

## [2.26.0](https://github.com/qsy123-coder/JASM/compare/v2.25.0...v2.26.0) (2026-09-08)


### Features

* **market:** 卡片占位改纯色 + 加载动画 + 图片源离屏折叠 ([4d15cdb](https://github.com/qsy123-coder/JASM/commit/4d15cdb2de6be91a8998a0da7fa86b179accb7e6))
* **market:** 点开详情按 id 补拉 description,并丢弃过期异步结果 ([cbe339e](https://github.com/qsy123-coder/JASM/commit/cbe339e3139a0540c2a4afec83ac5f2c9c7f9493))
* **market:** 视口懒加载图片源,滚到才设 Source 减少并发 COS 拉图 ([df09867](https://github.com/qsy123-coder/JASM/commit/df09867a99e5bee6e975936242683a72609e845c))
* **market:** 详情描述 TextBlock 命名,作为补拉 description 的回填目标 ([1834f7a](https://github.com/qsy123-coder/JASM/commit/1834f7a54dfc39c4c52d8f26493c58d9f2222ab5))


### Bug Fixes

* **update:** AutoUpdater 下载源与回退链接改指 fork 仓库 ([bd567e3](https://github.com/qsy123-coder/JASM/commit/bd567e3fc9367aeb236bbbe12f28431daa15a306))
* **update:** UpdateChecker 更新检测改指 fork 仓库 ([f9ccc3d](https://github.com/qsy123-coder/JASM/commit/f9ccc3de84dfa113b2e4c7d90c78447020ebb0e9))


### Performance Improvements

* **market:** ModMarketService 列表用窄字段投影 + 分类计数短TTL缓存 ([6a70c65](https://github.com/qsy123-coder/JASM/commit/6a70c65d8baed484800d0a87158216d9aa54c3e3))


### Miscellaneous

* **gitignore:** 忽略 Release.py 构建输出目录 output/ ([39528f0](https://github.com/qsy123-coder/JASM/commit/39528f019bd7317e7f745ea8fc781e6faa9d2664))
* **release:** bump version 2.25.0 -&gt; 2.26.0 ([50f7ec8](https://github.com/qsy123-coder/JASM/commit/50f7ec87c162432fd1d586dd0dfb2dbd4e96d32e))


### Continuous Integration

* **release:** release-please 监听 master 以匹配默认分支 ([c26c525](https://github.com/qsy123-coder/JASM/commit/c26c525a6641d2387df8eebc62f1c9d80aaf3627))

## [2.25.0](https://github.com/qsy123-coder/JASM/compare/v2.24.0...v2.25.0) (2026-09-06)


### Fixes

* 游戏数据同步不再覆盖 `game.json` 与 `.dataversion`,修复了「一键配置 Mod 环境」按钮在 2.24.0 消失的问题(由 2.24.0 之前打包的旧数据包在同步时覆盖了内置的 game.json 导致)
* 游戏数据打包脚本不再把 `game.json` 与 `.dataversion` 打进数据包,从源头杜绝数据包覆盖静态配置

## [2.24.0](https://github.com/qsy123-coder/JASM/compare/v2.23.0...v2.24.0) (2026-08-29)


### Features

* Downloads are now resilient on weak networks: automatic retry with exponential backoff (max 3 attempts), resumable downloads from `.part` files, a 30s stall timeout that auto-resumes, throttled progress with speed display, and stale `.part` cleanup
* One-click Mod environment setup keeps already-installed packages when a later package fails: an incremental `.modenv.json` marker is written after each package
* The setup wizard now shows a "Test Launch" button after a successful configuration to launch the game with mods in one click
* Added the Denia (达妮娅) character to the Wuthering Waves character list

## [2.23.0](https://github.com/qsy123-coder/JASM/compare/v2.22.9...v2.23.0) (2026-08-29)


### Features

* One-click Mod environment setup for Wuthering Waves: auto-detects the game install, downloads & installs the XXMI injector, WWMi game package and the XXMI Launcher (GUI) from a China-friendly CDN, with resumable downloads, SHA256 verification and idempotent repair/update
* One-click setup auto-fills the importer/mods paths and the launcher's game folder, creates a desktop shortcut, and fixes the launcher's UTF-8 BOM "load config failed" dialog
* One-click setup now auto-configures the game start commands: "Start Game" launches the game with mods via `XXMI Launcher.exe --xxmi WWMI --nogui`, and "Start 3Dmigoto" opens the launcher GUI

## [2.22.9](https://github.com/Jorixon/JASM/compare/v2.22.8...v2.22.9) (2026-01-18)


### Miscellaneous

* Added characters for Genshin, HSR, WuWa and ZZZ ([#408](https://github.com/Jorixon/JASM/issues/408)) ([ae293bc](https://github.com/Jorixon/JASM/commit/ae293bce2f3c2a1a2c0db2535c48324cf4ea3bc3))

## [2.22.8](https://github.com/Jorixon/JASM/compare/v2.22.7...v2.22.8) (2025-11-15)


### Miscellaneous

* Added missing skins and characters for Genshin, HSR, ZZZ and WuWa ([#399](https://github.com/Jorixon/JASM/issues/399)) ([8af5af9](https://github.com/Jorixon/JASM/commit/8af5af9653752cfc95d2a15310615f59fdc9e482))

## [2.22.7](https://github.com/Jorixon/JASM/compare/v2.22.6...v2.22.7) (2025-07-01)


### Miscellaneous

* Added character skins for WuWa and added new characters for WuWa and Honkai Star Rail ([#393](https://github.com/Jorixon/JASM/issues/393)) ([cb34add](https://github.com/Jorixon/JASM/commit/cb34addbfbf021e1c8f6fd6c5401cb6a2cae8cf1))

## [2.22.6](https://github.com/Jorixon/JASM/compare/v2.22.5...v2.22.6) (2025-06-16)


### Miscellaneous

* Added ZZZ Characters and Genshin Characters ([#388](https://github.com/Jorixon/JASM/issues/388)) ([a2b806f](https://github.com/Jorixon/JASM/commit/a2b806fba902394e6e9cf3bb72fdb148adc198e6))

## [2.22.5](https://github.com/Jorixon/JASM/compare/v2.22.4...v2.22.5) (2025-05-25)


### Bug Fixes

* Window size is now saved and restored correctly when using DPI scaling ([#384](https://github.com/Jorixon/JASM/issues/384)) ([160b156](https://github.com/Jorixon/JASM/commit/160b1566a44671e103fb7f755553c4348efecf7e))


### Miscellaneous

* Added Hugo, Missing WuWa skins, Current and upcoming patch Star Rail and WuWa characters ([#383](https://github.com/Jorixon/JASM/issues/383)) ([df20a59](https://github.com/Jorixon/JASM/commit/df20a59630f0441bf6f246d72713af5ac3e03673))
* Updated release dates for Star Rail, WuWa and ZZZ characters ([9ed4f58](https://github.com/Jorixon/JASM/commit/9ed4f587cf7670a463efac5c151e55f04bf5596a))

## [2.22.4](https://github.com/Jorixon/JASM/compare/v2.22.3...v2.22.4) (2025-05-13)


### Miscellaneous

* Updated RU locale for Genshin, ZZZ and HSR ([#378](https://github.com/Jorixon/JASM/issues/378)) ([d332d98](https://github.com/Jorixon/JASM/commit/d332d98b56e9336c269f193d886a044112aeaf45))

## [2.22.3](https://github.com/Jorixon/JASM/compare/v2.22.2...v2.22.3) (2025-05-11)


### Miscellaneous

* Added Vivian, Escoffier and missing Genshin skins ([#377](https://github.com/Jorixon/JASM/issues/377)) ([8735754](https://github.com/Jorixon/JASM/commit/87357544eade0a7ef6767692cd1af313576ae0b8))

## [2.22.2](https://github.com/Jorixon/JASM/compare/v2.22.1...v2.22.2) (2025-04-20)


### Bug Fixes

* JASM crashing on startup if there is a custom character with same internal name as another moddableObject (npc,weapon,etc) ([254ab3a](https://github.com/Jorixon/JASM/commit/254ab3af585ff2f1828a03694e378794026b63e6))

## [2.22.1](https://github.com/Jorixon/JASM/compare/v2.22.0...v2.22.1) (2025-04-20)


### Miscellaneous

* Updated WinAppSDK to 1.7.1 ([#370](https://github.com/Jorixon/JASM/issues/370)) ([1143fc3](https://github.com/Jorixon/JASM/commit/1143fc354212d1c58504a114bfffeea2520783eb))
* **Genshin:** Updated images and release dates for Iansan and Varesa ([#370](https://github.com/Jorixon/JASM/issues/370)) ([1143fc3](https://github.com/Jorixon/JASM/commit/1143fc354212d1c58504a114bfffeea2520783eb))

## [2.22.0](https://github.com/Jorixon/JASM/compare/v2.21.1...v2.22.0) (2025-03-09)


### Features

* **gallery:** Support paste image and save at the gallery page ([#349](https://github.com/Jorixon/JASM/issues/349)) ([776275b](https://github.com/Jorixon/JASM/commit/776275bf7376e5ccb5365f8f3f7915d36f1fb04c))


### Bug Fixes

* Pinned characters not being moved to the top on character overview page ([#355](https://github.com/Jorixon/JASM/issues/355)) ([0388d59](https://github.com/Jorixon/JASM/commit/0388d5965a4069586ce2f8fb2a1c0e624089f54d))


### Miscellaneous

* **Genshin:** Added Iansan and Varesa ([#352](https://github.com/Jorixon/JASM/issues/352)) ([97a7389](https://github.com/Jorixon/JASM/commit/97a738993836888bd1fd6e0aefb6ba0590662869))
* **Honkai:** Added Castorice and Anaxa ([#352](https://github.com/Jorixon/JASM/issues/352)) ([97a7389](https://github.com/Jorixon/JASM/commit/97a738993836888bd1fd6e0aefb6ba0590662869))
* **WuWa:** Added Cantarella ([#352](https://github.com/Jorixon/JASM/issues/352)) ([97a7389](https://github.com/Jorixon/JASM/commit/97a738993836888bd1fd6e0aefb6ba0590662869))

## [2.21.1](https://github.com/Jorixon/JASM/compare/v2.21.0...v2.21.1) (2025-02-16)


### Miscellaneous

* **Genshin:** Added Mizuki ([#339](https://github.com/Jorixon/JASM/issues/339)) ([75926b2](https://github.com/Jorixon/JASM/commit/75926b2b3c4da211f833037c2da96bfd420b3718))
* **Honkai:** Added March Nascent Spring skin, Mem and Garmentmaker ([#339](https://github.com/Jorixon/JASM/issues/339)) ([75926b2](https://github.com/Jorixon/JASM/commit/75926b2b3c4da211f833037c2da96bfd420b3718))
* **ZZZ:** Added Pulchra, SS Anby and Trigger ([#339](https://github.com/Jorixon/JASM/issues/339)) ([75926b2](https://github.com/Jorixon/JASM/commit/75926b2b3c4da211f833037c2da96bfd420b3718))
* Updated winAppSdk to 1.6.5 ([#345](https://github.com/Jorixon/JASM/issues/345)) ([efcdcec](https://github.com/Jorixon/JASM/commit/efcdceccf7f5af75c0017217ef5523bc18121a11))

## [2.21.0](https://github.com/Jorixon/JASM/compare/v2.20.0...v2.21.0) (2025-01-19)


### Features

* **preset:** Support randomizing mods at characters page ([#331](https://github.com/Jorixon/JASM/issues/331)) ([7932b96](https://github.com/Jorixon/JASM/commit/7932b96e2c02038f278a768861ded13711af5dd7))


### Bug Fixes

* Occasional issue where some mods were not added when creating preset ([#333](https://github.com/Jorixon/JASM/issues/333)) ([a9edda0](https://github.com/Jorixon/JASM/commit/a9edda013703d5d9a40203d6f62f81a5bc4ffab9))


### Miscellaneous

* Updated WinAppSDK to 1.6.4 and project packages ([#333](https://github.com/Jorixon/JASM/issues/333)) ([a9edda0](https://github.com/Jorixon/JASM/commit/a9edda013703d5d9a40203d6f62f81a5bc4ffab9))

## [2.20.0](https://github.com/Jorixon/JASM/compare/v2.19.0...v2.20.0) (2025-01-12)


### Features

* Added two new context items to the character overview right click menu. "Disable all mods" and "Open Folder..." ([#327](https://github.com/Jorixon/JASM/issues/327)) ([ed89b08](https://github.com/Jorixon/JASM/commit/ed89b0899377d8cf5516b460fe16df5d98312834))


### Bug Fixes

* When navigating back from the character details page to the character overview, it will now correctly scroll the character into view ([#327](https://github.com/Jorixon/JASM/issues/327)) ([ed89b08](https://github.com/Jorixon/JASM/commit/ed89b0899377d8cf5516b460fe16df5d98312834))


### Miscellaneous

* Added a button to open the cached download folder that JASM uses on the settings page ([#327](https://github.com/Jorixon/JASM/issues/327)) ([ed89b08](https://github.com/Jorixon/JASM/commit/ed89b0899377d8cf5516b460fe16df5d98312834))
* **Genshin:** Updated some existing character's images ([#327](https://github.com/Jorixon/JASM/issues/327)) ([ed89b08](https://github.com/Jorixon/JASM/commit/ed89b0899377d8cf5516b460fe16df5d98312834))
* Removed old legacy character details page ([#327](https://github.com/Jorixon/JASM/issues/327)) ([ed89b08](https://github.com/Jorixon/JASM/commit/ed89b0899377d8cf5516b460fe16df5d98312834))

## [2.19.0](https://github.com/Jorixon/JASM/compare/v2.18.8...v2.19.0) (2025-01-11)


### Features

* Add support for creating custom characters within JASM on the Character management page ([#315](https://github.com/Jorixon/JASM/issues/315)) ([24a9429](https://github.com/Jorixon/JASM/commit/24a9429bb478fccb5233264626f7588745f6a21b))


### Miscellaneous

* Minor performance optimizations ([24a9429](https://github.com/Jorixon/JASM/commit/24a9429bb478fccb5233264626f7588745f6a21b))
* When selecting the .ini file for a mod in the mod pane, the mod's folder path will be copied to clipboard ([24a9429](https://github.com/Jorixon/JASM/commit/24a9429bb478fccb5233264626f7588745f6a21b))


### Code Refactoring

* Changed naming scheme of Genshin image files ([24a9429](https://github.com/Jorixon/JASM/commit/24a9429bb478fccb5233264626f7588745f6a21b))

## [2.18.8](https://github.com/Jorixon/JASM/compare/v2.18.7...v2.18.8) (2025-01-09)


### Miscellaneous

* Added Mydei and various WuWa characters ([#319](https://github.com/Jorixon/JASM/issues/319)) ([8548692](https://github.com/Jorixon/JASM/commit/8548692e815c15e8dc8e7ca623e81713ced7988b))

## [2.18.7](https://github.com/Jorixon/JASM/compare/v2.18.6...v2.18.7) (2024-12-30)


### Miscellaneous

* **Honkai:** Added character Tribbie ([#317](https://github.com/Jorixon/JASM/issues/317)) ([66acb47](https://github.com/Jorixon/JASM/commit/66acb4741f53396002606f2cd569cc1672d9a54c))

## [2.18.6](https://github.com/Jorixon/JASM/compare/v2.18.5...v2.18.6) (2024-12-21)


### Bug Fixes

* Unable to set mod preview image by pasting a file image ([#313](https://github.com/Jorixon/JASM/issues/313)) ([572ab0c](https://github.com/Jorixon/JASM/commit/572ab0cff77e40ece137c16919396384caca1d02))

## [2.18.5](https://github.com/Jorixon/JASM/compare/v2.18.4...v2.18.5) (2024-12-21)


### Miscellaneous

* **zzz:** Added lighter and updated release dates of some characters ([#311](https://github.com/Jorixon/JASM/issues/311)) ([82b20ae](https://github.com/Jorixon/JASM/commit/82b20aeb31e2e9392816919f5741f2bebd64b682))

## [2.18.4](https://github.com/Jorixon/JASM/compare/v2.18.3...v2.18.4) (2024-12-17)


### Miscellaneous

* **Genshin:** Updated character Citlali Info and added characters Mavuika and Lan Yan ([#307](https://github.com/Jorixon/JASM/issues/307)) ([3f40d5b](https://github.com/Jorixon/JASM/commit/3f40d5bfbdb7a37eb829063a30180877c4552b5a))
* **Honkai:** Added character Aglaea ([#307](https://github.com/Jorixon/JASM/issues/307)) ([3f40d5b](https://github.com/Jorixon/JASM/commit/3f40d5bfbdb7a37eb829063a30180877c4552b5a))
* **WuWa:** Added character Lumi ([#307](https://github.com/Jorixon/JASM/issues/307)) ([3f40d5b](https://github.com/Jorixon/JASM/commit/3f40d5bfbdb7a37eb829063a30180877c4552b5a))
* **ZZZ:** Added characters Harumasa, Astra and Evelyn ([#307](https://github.com/Jorixon/JASM/issues/307)) ([3f40d5b](https://github.com/Jorixon/JASM/commit/3f40d5bfbdb7a37eb829063a30180877c4552b5a))

## [2.18.3](https://github.com/Jorixon/JASM/compare/v2.18.2...v2.18.3) (2024-11-23)


### Miscellaneous

* Added 'The Herta' ([#298](https://github.com/Jorixon/JASM/issues/298)) ([0e27b0a](https://github.com/Jorixon/JASM/commit/0e27b0a67dbaa349e6b9e727d427f329744ef146))
* Updated WinAppSdk to 1.6.3 ([#300](https://github.com/Jorixon/JASM/issues/300)) ([b93d6ce](https://github.com/Jorixon/JASM/commit/b93d6ce3f6f36cc2ea8a4576b3bc7602b7ecd294))

## [2.18.2](https://github.com/Jorixon/JASM/compare/v2.18.1...v2.18.2) (2024-11-18)


### Miscellaneous

* **Honkai:** Added Rappa ([#296](https://github.com/Jorixon/JASM/issues/296)) ([6a9de77](https://github.com/Jorixon/JASM/commit/6a9de778eb6ebdabf79d9a244e827672f63fc981))
* Updated to .NET 9 ([b247e30](https://github.com/Jorixon/JASM/commit/b247e30c3efd66abd6529833f2ed0e76106562d1))

## [2.18.1](https://github.com/Jorixon/JASM/compare/v2.18.0...v2.18.1) (2024-11-15)


### Miscellaneous

* Add Camellya, Fugue, and Sunday ([#292](https://github.com/Jorixon/JASM/issues/292)) ([b75ad63](https://github.com/Jorixon/JASM/commit/b75ad6334a12b180f22ec92ca54b3a46c2bdb471))

## [2.18.0](https://github.com/Jorixon/JASM/compare/v2.17.1...v2.18.0) (2024-11-10)


### Features

* Added disable all mods button on settings page ([5c617df](https://github.com/Jorixon/JASM/commit/5c617df0885c7d69ca07c7bc35110c9374196b8f))
* Added disable all mods for character button to character details page ([#287](https://github.com/Jorixon/JASM/issues/287)) ([5c617df](https://github.com/Jorixon/JASM/commit/5c617df0885c7d69ca07c7bc35110c9374196b8f))
* Added filter option to only show characters with at least one enabled mod ([5c617df](https://github.com/Jorixon/JASM/commit/5c617df0885c7d69ca07c7bc35110c9374196b8f))
* Show number of mods enabled on the character card and show colored underline if any mods are enabled ([5c617df](https://github.com/Jorixon/JASM/commit/5c617df0885c7d69ca07c7bc35110c9374196b8f))


### Bug Fixes

* Potential fix for drag and drop not working for WinRAR ([#288](https://github.com/Jorixon/JASM/issues/288)) ([45026dc](https://github.com/Jorixon/JASM/commit/45026dc1934f051d2217f8f4d892123ecbf061fe))


### Miscellaneous

* Updated ui text for description field on the mod installer window ([ddbc628](https://github.com/Jorixon/JASM/commit/ddbc6283f4b34eaba3cf1647b3b122864ddd83fe))

## [2.17.1](https://github.com/Jorixon/JASM/compare/v2.17.0...v2.17.1) (2024-11-08)


### Miscellaneous

* **ZZZ:** Add Yanagi ([#282](https://github.com/Jorixon/JASM/issues/282)) ([626f507](https://github.com/Jorixon/JASM/commit/626f507ca2b630b2fc2042620ce9dfd11935e279))

## [2.17.0](https://github.com/Jorixon/JASM/compare/v2.16.3...v2.17.0) (2024-11-04)


### Features

* Rewrite code for CharacterDetailsPage ([#262](https://github.com/Jorixon/JASM/issues/262)) ([1bb8a6c](https://github.com/Jorixon/JASM/commit/1bb8a6cd2d7d1d9756b1252319e28b3f029c9aaa))


### Bug Fixes

* Don't remove mod notification if closing window while loading mod info ([1bb8a6c](https://github.com/Jorixon/JASM/commit/1bb8a6cd2d7d1d9756b1252319e28b3f029c9aaa))

## [2.16.3](https://github.com/Jorixon/JASM/compare/v2.16.2...v2.16.3) (2024-10-11)


### Miscellaneous

* Fix name for Ororon ([#273](https://github.com/Jorixon/JASM/issues/273)) ([baab6b5](https://github.com/Jorixon/JASM/commit/baab6b55c04a72738c5753a31feb6013db064b52))

## [2.16.2](https://github.com/Jorixon/JASM/compare/v2.16.1...v2.16.2) (2024-10-11)


### Miscellaneous

* Add Genshin 5.1 and 5.2 characters ([#270](https://github.com/Jorixon/JASM/issues/270)) ([3eb44b4](https://github.com/Jorixon/JASM/commit/3eb44b418ad32db7708ce274ffda9a0b16001299))
* Updated packages and winappsdk ([#271](https://github.com/Jorixon/JASM/issues/271)) ([4a22f2d](https://github.com/Jorixon/JASM/commit/4a22f2d4778593f91a5686f173b49cefe21694fb))

## [2.16.1](https://github.com/Jorixon/JASM/compare/v2.16.0...v2.16.1) (2024-10-05)


### Miscellaneous

* **WuWa:** Added Shorekeeper and Youhu ([#267](https://github.com/Jorixon/JASM/issues/267)) ([d7854e1](https://github.com/Jorixon/JASM/commit/d7854e19c0c395de026a91598c3ee8c3b98b7fca))

## [2.16.0](https://github.com/Jorixon/JASM/compare/v2.15.0...v2.16.0) (2024-09-21)


### Features

* Allow for automatically replacing an existing mod in a preset with a new mod when installing new mods ([#258](https://github.com/Jorixon/JASM/issues/258)) ([65b580b](https://github.com/Jorixon/JASM/commit/65b580b73534914ebf448fdbd38f3f2af0852677))
* For the CharacterDetailsPage grid ordering is persisted in memory ([65b580b](https://github.com/Jorixon/JASM/commit/65b580b73534914ebf448fdbd38f3f2af0852677))
* Now possible to filter to only characters where there is a mod notification i.e. Mod update / new mod added. New filter dropdown added to the left of the Element icons. ([65b580b](https://github.com/Jorixon/JASM/commit/65b580b73534914ebf448fdbd38f3f2af0852677))
* Now possible to start JASM and switch to specific game trough command line args. See FAQ for example ([65b580b](https://github.com/Jorixon/JASM/commit/65b580b73534914ebf448fdbd38f3f2af0852677))


### Miscellaneous

* Added link to Github releases on the settings page when a new update is available ([65b580b](https://github.com/Jorixon/JASM/commit/65b580b73534914ebf448fdbd38f3f2af0852677))
* Current size of the local mod cache is now shown on the settings page ([65b580b](https://github.com/Jorixon/JASM/commit/65b580b73534914ebf448fdbd38f3f2af0852677))
* When updating a mod, the existing JASM_ModConfig will take precedence over settings taken from GameBanana ([65b580b](https://github.com/Jorixon/JASM/commit/65b580b73534914ebf448fdbd38f3f2af0852677))

## [2.15.0](https://github.com/Jorixon/JASM/compare/v2.14.3...v2.15.0) (2024-09-13)


### Features

* **Genshin:** Added Nilou skin and Kirara skin ([c17fe45](https://github.com/Jorixon/JASM/commit/c17fe45574d27044d9a21dbb4ee862b2c51bce4c))
* Save space in CharacterDetailsPage by making the first and second columns use less space ([#251](https://github.com/Jorixon/JASM/issues/251)) ([2204c06](https://github.com/Jorixon/JASM/commit/2204c06ab5f10915571bbfe6861f2084688d1cb5))
* Updated Simplified Chinese Translation ([#247](https://github.com/Jorixon/JASM/issues/247)) ([6c2aa4c](https://github.com/Jorixon/JASM/commit/6c2aa4cf3316c624c7fc1008bf7a8d871ab84d25))
* **ZZZ:** Added partial Spanish (Argentina) translation ([#238](https://github.com/Jorixon/JASM/issues/238)) ([b48ccf3](https://github.com/Jorixon/JASM/commit/b48ccf312a6e10c8e46e5d700a9dcf247054c118))


### Miscellaneous

* Updated application packages ([#254](https://github.com/Jorixon/JASM/issues/254)) ([c17fe45](https://github.com/Jorixon/JASM/commit/c17fe45574d27044d9a21dbb4ee862b2c51bce4c))
* Updated WinAppSDK to 1.6 ([c17fe45](https://github.com/Jorixon/JASM/commit/c17fe45574d27044d9a21dbb4ee862b2c51bce4c))

## [2.14.3](https://github.com/Jorixon/JASM/compare/v2.14.2...v2.14.3) (2024-08-26)


### Miscellaneous

* Exclude Elevator.exe from default release build ([#245](https://github.com/Jorixon/JASM/issues/245)) ([d699f7a](https://github.com/Jorixon/JASM/commit/d699f7a23ea5d7af3646cad99e1bca30c1f6fadd))
* **WuWu:** Added Zhezhi and Xiangliyao ([#244](https://github.com/Jorixon/JASM/issues/244)) Thanks @Moonholder ([6fb1f42](https://github.com/Jorixon/JASM/commit/6fb1f428b51844db8fc04b0f43f6dab1654de3da))

## [2.14.2](https://github.com/Jorixon/JASM/compare/v2.14.1...v2.14.2) (2024-08-20)


### Miscellaneous

* Revert package update in Elevator, might help with AV incorrect detection ([#239](https://github.com/Jorixon/JASM/issues/239)) ([22378a6](https://github.com/Jorixon/JASM/commit/22378a63299e53582c40f346fba1d6b792982513))

## [2.14.1](https://github.com/Jorixon/JASM/compare/v2.14.0...v2.14.1) (2024-08-20)


### Miscellaneous

* **ZZZ:** Added Burnice ([#235](https://github.com/Jorixon/JASM/issues/235)) Thanks @Pyrageis ([42e42e0](https://github.com/Jorixon/JASM/commit/42e42e0aac4e0fd57bc56ceffe67ef47fdb9693d))

## [2.14.0](https://github.com/Jorixon/JASM/compare/v2.13.3...v2.14.0) (2024-08-11)


### Features

* Create and run custom commands from within JASM ([#222](https://github.com/Jorixon/JASM/issues/222)) ([5ea97ef](https://github.com/Jorixon/JASM/commit/5ea97efcc2b03f86562d1f5349b948c35224150a))


### Miscellaneous

* Added missing ZZZ characters ([5ea97ef](https://github.com/Jorixon/JASM/commit/5ea97efcc2b03f86562d1f5349b948c35224150a))

## [2.13.3](https://github.com/Jorixon/JASM/compare/v2.13.2...v2.13.3) (2024-08-04)


### Miscellaneous

* Now possible to toggle whether JASM remembers window size and window position ([#229](https://github.com/Jorixon/JASM/issues/229)) ([79f901f](https://github.com/Jorixon/JASM/commit/79f901f31b44bda2c8be468adfe94a474894d3ca))

## [2.13.2](https://github.com/Jorixon/JASM/compare/v2.13.1...v2.13.2) (2024-08-02)


### Miscellaneous

* Added more error handling to Auto Updater application ([05b3339](https://github.com/Jorixon/JASM/commit/05b3339447c8f0c72b18e117e4247d6144d7346f))

## [2.13.1](https://github.com/Jorixon/JASM/compare/v2.13.0...v2.13.1) (2024-08-01)


### Miscellaneous

* Add characters for Genshin (4.8 - 5.0) and HSR (2.4 - 2.5) ([#224](https://github.com/Jorixon/JASM/issues/224)) Thanks [@jeffvli](https://github.com/jeffvli) ([39b5dd7](https://github.com/Jorixon/JASM/commit/39b5dd7e86f0cefdbe9fc97127dd7007eaee01e6))
* Update Genshin Game Localization(zh-cn) to 4.8 ([#223](https://github.com/Jorixon/JASM/issues/223)) Thanks [@kanonmelodis](https://github.com/kanonmelodis) ([3ff9e30](https://github.com/Jorixon/JASM/commit/3ff9e305f5dc9439e27178eb4c01556bd82eddd2))

## [2.13.0](https://github.com/Jorixon/JASM/compare/v2.12.2...v2.13.0) (2024-07-25)


### Features

* Add sort options to character gallery view ([#215](https://github.com/Jorixon/JASM/issues/215)) Thanks [@jeffvli](https://github.com/jeffvli) ([c4c02f3](https://github.com/Jorixon/JASM/commit/c4c02f3f273458d8dae2c35c4d771c1eb1a4c802))
* Support deleting mod in gallery view ([#213](https://github.com/Jorixon/JASM/issues/213)) Thanks [@yuukidach](https://github.com/yuukidach) ([4a1744e](https://github.com/Jorixon/JASM/commit/4a1744ec8705290c55d6aa81336b4a63c5551c55))

## [2.12.2](https://github.com/Jorixon/JASM/compare/v2.12.1...v2.12.2) (2024-07-23)


### Miscellaneous

* Added all supported games to the quick switch menu (https://github.com/Jorixon/JASM/issues/210) Thanks [@jeffvli](https://github.com/jeffvli) ([afcd8a8](https://github.com/Jorixon/JASM/commit/afcd8a8ea850373477ed67394aed05d73e4832e4))


### Code Refactoring

* Limited the number of active tasks queued at the same time in ModUpdateAvailableChecker. This should improve performance when checking for mod updates with a large number of mods. ([#214](https://github.com/Jorixon/JASM/issues/214)) ([8364204](https://github.com/Jorixon/JASM/commit/8364204fd2fc873f8eb96c05584760325ac3bc1e))

## [2.12.1](https://github.com/Jorixon/JASM/compare/v2.12.0...v2.12.1) (2024-07-23)


### Miscellaneous

* Added Russian translation to Genshin and Honkai game related text ([d7f7751](https://github.com/Jorixon/JASM/commit/d7f77512a74105911ee1ad249c8134ec27b8eccc))

## [2.12.0](https://github.com/Jorixon/JASM/compare/v2.11.0...v2.12.0) (2024-07-11)


### Features

* Added ZZZ support ([#205](https://github.com/Jorixon/JASM/issues/205)) Thanks @Pyrageis ([41b2497](https://github.com/Jorixon/JASM/commit/41b24979f8e86a4a245f45a699101ce582d26403))


### Bug Fixes

* Mod update notification would always be shown for first update check for new mod ([#205](https://github.com/Jorixon/JASM/issues/205)) ([86a96e5](https://github.com/Jorixon/JASM/commit/86a96e5214b6d828d9306e5e0940b96f84c8c094))


### Miscellaneous

* Added logging to auto updater ([86f0697](https://github.com/Jorixon/JASM/commit/86f06975a0303cfabf61e079f627ea503391cda4))
* Changed validation check for model import loader exe name ([#205](https://github.com/Jorixon/JASM/issues/205)) ([86a96e5](https://github.com/Jorixon/JASM/commit/86a96e5214b6d828d9306e5e0940b96f84c8c094))

## [2.11.0](https://github.com/Jorixon/JASM/compare/v2.10.1...v2.11.0) (2024-07-04)


### Features

* Non-fatal exceptions no longer close the main window ([#203](https://github.com/Jorixon/JASM/issues/203)) ([8c7bd16](https://github.com/Jorixon/JASM/commit/8c7bd164cf582740ca7b3d08e1fc1e63df6f6369))


### Miscellaneous

* Added some Russian translations. Thanks for the help Haosy ([8c7bd16](https://github.com/Jorixon/JASM/commit/8c7bd164cf582740ca7b3d08e1fc1e63df6f6369))
* Updated most app packages including WinAppSdk ([8c7bd16](https://github.com/Jorixon/JASM/commit/8c7bd164cf582740ca7b3d08e1fc1e63df6f6369))


### Code Refactoring

* Removed old code that was used to make api calls and check for mod updates ([8c7bd16](https://github.com/Jorixon/JASM/commit/8c7bd164cf582740ca7b3d08e1fc1e63df6f6369))

## [2.10.1](https://github.com/Jorixon/JASM/compare/v2.10.0...v2.10.1) (2024-06-06)


### Miscellaneous

* Updated "reorganize mods" tooltip on startup page ([4288708](https://github.com/Jorixon/JASM/commit/42887087cba0ca8f164b1664a78f58de7469cba9))

## [2.10.0](https://github.com/Jorixon/JASM/compare/v2.9.1...v2.10.0) (2024-06-05)


### Features

* Wuthering Waves support ([#192](https://github.com/Jorixon/JASM/issues/192)) ([697ccf7](https://github.com/Jorixon/JASM/commit/697ccf745434b402e022f2ccc9d442ae0b41fdef))

## [2.9.1](https://github.com/Jorixon/JASM/compare/v2.9.0...v2.9.1) (2024-06-03)


### Miscellaneous

* **Genshin:** Moved Clorinde and Sigewinne to characters, added Sethos character and added missing weapons ([#190](https://github.com/Jorixon/JASM/issues/190)) ([1c11e31](https://github.com/Jorixon/JASM/commit/1c11e31ff650276ee501ae9d65313c8dcc102764))
* Updated WinAppSdk to 1.5.3 ([b8c8d61](https://github.com/Jorixon/JASM/commit/b8c8d61754ce60645ee42bb10ed53b9348bbb004))

## [2.9.0](https://github.com/Jorixon/JASM/compare/v2.8.0...v2.9.0) (2024-05-05)


### Features

* Added first iteration of the mod gallery view ([#180](https://github.com/Jorixon/JASM/issues/180)) ([461a91f](https://github.com/Jorixon/JASM/commit/461a91fa3caf97877673cfa15c9b69e0eff03229))


### Miscellaneous

* Added HSR 2.2-2.3 characters ([#182](https://github.com/Jorixon/JASM/issues/182)) ([3c519cc](https://github.com/Jorixon/JASM/commit/3c519ccff7d59f91b826a2fab7df66e2cbdefb5d))
* **ModInstaller:** "Enable only this mod" checkbox defaults to off for multi mod characters ([9aa90a9](https://github.com/Jorixon/JASM/commit/9aa90a9fb1217029051599b3e00146e293bc7ccd))

## [2.8.0](https://github.com/Jorixon/JASM/compare/v2.7.0...v2.8.0) (2024-04-21)


### Features

* Now possible to download mods directly in the "Update available" / "New mod files" window ([#177](https://github.com/Jorixon/JASM/issues/177)) ([8c7ed5f](https://github.com/Jorixon/JASM/commit/8c7ed5f3fe81f347d6da03c29906f28430b15cac))

## [2.7.0](https://github.com/Jorixon/JASM/compare/v2.6.3...v2.7.0) (2024-04-20)


### Features

* Now possible to quickly switch presets from the characters overview page ([#176](https://github.com/Jorixon/JASM/issues/176)) ([9731655](https://github.com/Jorixon/JASM/commit/9731655d86ae70b079d0c24b87c8beb490541983))


### Miscellaneous

* Updated WinAppSdk and a few other packages ([#174](https://github.com/Jorixon/JASM/issues/174)) ([4289cda](https://github.com/Jorixon/JASM/commit/4289cda936d9c4d88fabb4d2f9caf981a1e5f360))


### Continuous Integration

* Added Self Contained build to releases ([9731655](https://github.com/Jorixon/JASM/commit/9731655d86ae70b079d0c24b87c8beb490541983))

## [2.6.3](https://github.com/Jorixon/JASM/compare/v2.6.2...v2.6.3) (2024-04-01)


### Miscellaneous

* Improved mod enabling logic during mod install ([#167](https://github.com/Jorixon/JASM/issues/167)) ([7498afb](https://github.com/Jorixon/JASM/commit/7498afb83948ad4967c50266b3d354637909c601)) Thanks @Davoleo 

## [2.6.2](https://github.com/Jorixon/JASM/compare/v2.6.1...v2.6.2) (2024-03-31)


### Miscellaneous

* Added Waverider and Xingqiu skin Bamboo Rain ([#168](https://github.com/Jorixon/JASM/issues/168)) ([c978d06](https://github.com/Jorixon/JASM/commit/c978d069434a837c487164fc387370f93b875eb4))
* Changed NPC and Weapon icons ([2176bfb](https://github.com/Jorixon/JASM/commit/2176bfbcf16f6fb1bb5bab33933fdcc97c3869ba))

## [2.6.1](https://github.com/Jorixon/JASM/compare/v2.6.0...v2.6.1) (2024-03-29)


### Bug Fixes

* Pasting image from clipboard was saved as .bitmap when .png format was available ([81eb571](https://github.com/Jorixon/JASM/commit/81eb571c68a24fd8ffb180974538776b9c3837f7))


### Miscellaneous

* Added presets overview ([#162](https://github.com/Jorixon/JASM/issues/162)) ([442a164](https://github.com/Jorixon/JASM/commit/442a16470478e35bcf1cff999cfed41a5d31a39b))
* Possible set preset as Read Only ([442a164](https://github.com/Jorixon/JASM/commit/442a16470478e35bcf1cff999cfed41a5d31a39b))
* Possible to manually retrieve/refresh mod info when installing a mod ([668883c](https://github.com/Jorixon/JASM/commit/668883c607847c45125c1529253d2c33e1f4e9b6))

## [2.6.0](https://github.com/Jorixon/JASM/compare/v2.5.0...v2.6.0) (2024-03-26)


### Features

* Mod Presets and Persisting of Mod Preferences ([#160](https://github.com/Jorixon/JASM/issues/160)) ([2b0bc5e](https://github.com/Jorixon/JASM/commit/2b0bc5e930987f290b59a8e708f4fc138fd1138c))


### Miscellaneous

* Detect Script.ini files ([78cd6f5](https://github.com/Jorixon/JASM/commit/78cd6f5048762de8fadedd1f536733707ccaae3f))

## [2.5.0](https://github.com/Jorixon/JASM/compare/v2.4.0...v2.5.0) (2024-03-23)


### Features

* Possible to pick, copy and paste mod image during mod install ([#157](https://github.com/Jorixon/JASM/issues/157)) ([b143296](https://github.com/Jorixon/JASM/commit/b14329630a6208fcb736f73f8cdcf218a62e7747))


### Bug Fixes

* Potential fix for NullReferenceException when navigating to character ([8cc90c2](https://github.com/Jorixon/JASM/commit/8cc90c2b840c447748e1adb6d4c70288ce4b2e4b))


### Miscellaneous

* Possible to set mod installer to always on top ([b143296](https://github.com/Jorixon/JASM/commit/b14329630a6208fcb736f73f8cdcf218a62e7747))
* Updated WinAppSDK and .NET ([#159](https://github.com/Jorixon/JASM/issues/159)) ([9bbd739](https://github.com/Jorixon/JASM/commit/9bbd739a0a404ad21f31a091a6de9a8944237d3a))

## [2.4.0](https://github.com/Jorixon/JASM/compare/v2.3.0...v2.4.0) (2024-03-23)


### Features

* Now possible to enable Character skins as separate characters ([#153](https://github.com/Jorixon/JASM/issues/153)) ([491f4bb](https://github.com/Jorixon/JASM/commit/491f4bb10aa6bdf3ea19bad416c8fa1dd8bedacf))


### Bug Fixes

* Check if WebView2 is available before using it ([#135](https://github.com/Jorixon/JASM/issues/135)) ([1bba6e6](https://github.com/Jorixon/JASM/commit/1bba6e6e77a8586678708e962b4e14a89608eac0))
* Not being able to set character override for mods ([#156](https://github.com/Jorixon/JASM/issues/156)) ([de28cca](https://github.com/Jorixon/JASM/commit/de28cca00fd8cdc5de203dbe20b497391bc6d456))


### Miscellaneous

* Added Arlecchino and various npcs ([#149](https://github.com/Jorixon/JASM/issues/149)) ([9b882e2](https://github.com/Jorixon/JASM/commit/9b882e229f0a144b604582f47f484758090db400))
* Added Verdict weapon ([#154](https://github.com/Jorixon/JASM/issues/154)) ([0be089e](https://github.com/Jorixon/JASM/commit/0be089e68ad7ce89208f7555f8be01b3f5c699be))

## [2.3.0](https://github.com/Jorixon/JASM/compare/v2.2.0...v2.3.0) (2024-03-13)


### Features

* Quick switch button added for switching between games ([#138](https://github.com/Jorixon/JASM/issues/138)) ([ec8adc2](https://github.com/Jorixon/JASM/commit/ec8adc2db04f51f4288ae952a92b832d257956ac))
* When navigating back from a character page to the character overview, it will now scroll that character into view ([ec8adc2](https://github.com/Jorixon/JASM/commit/ec8adc2db04f51f4288ae952a92b832d257956ac))


### Bug Fixes

* Potential fix for crash when navigating to character after mod install ([ec8adc2](https://github.com/Jorixon/JASM/commit/ec8adc2db04f51f4288ae952a92b832d257956ac))


### Miscellaneous

* Added Ganyu and Shenhe skins ([1499042](https://github.com/Jorixon/JASM/commit/1499042f3a138992f794421310d0e7285ea21e80))
* Redid Date Added sorting logic ([ec8adc2](https://github.com/Jorixon/JASM/commit/ec8adc2db04f51f4288ae952a92b832d257956ac))
* Reworked application cleanup and exit process ([#141](https://github.com/Jorixon/JASM/issues/141)) ([da9e65f](https://github.com/Jorixon/JASM/commit/da9e65f895e25fb8167ff178c5cc4fb9f6c0bf37))

## [2.2.0](https://github.com/Jorixon/JASM/compare/v2.1.2...v2.2.0) (2024-03-10)


### Reverts

* No longer publish as single file due to new (WinAppSDK?) bug ([#136](https://github.com/Jorixon/JASM/issues/136)) ([634692a](https://github.com/Jorixon/JASM/commit/634692a1b9d26f84fb2792b53f1fda5585359c67))


### Features

* Ability to choose .ini file for mods or to ignore it ([#126](https://github.com/Jorixon/JASM/issues/126)) ([8401d7d](https://github.com/Jorixon/JASM/commit/8401d7d41d712e57c3e1fe684aad92031561698b))


### Miscellaneous

* Added Chiori, hsr 2.1 characters, hsr character info, typo fixes ([#134](https://github.com/Jorixon/JASM/issues/134)) ([6f05ee6](https://github.com/Jorixon/JASM/commit/6f05ee672987f4079fb61254f658e06e37bef136)) Thanks @Pyrageis 
* Introduce Penacony and its characters ([#132](https://github.com/Jorixon/JASM/issues/132)) ([b59e3d9](https://github.com/Jorixon/JASM/commit/b59e3d92ebb4fc8ad4ebe5c4d1cbdbb1d7d35a33)) Thanks @EffortlessFury 
* Updated WinAppSdk to 1.5 and som other packages ([ae8947e](https://github.com/Jorixon/JASM/commit/ae8947e7a79cd2c204d7dd60e27eb33c7e082e9a))

## [2.1.2](https://github.com/Jorixon/JASM/compare/v2.1.1...v2.1.2) (2024-01-31)


### Miscellaneous

* Added characters Gaming and Xianyun ([0a41481](https://github.com/Jorixon/JASM/commit/0a41481b430584f4dd10a0a9241b5cd632b31ebb))

## [2.1.1](https://github.com/Jorixon/JASM/compare/v2.1.0...v2.1.1) (2024-01-28)


### Miscellaneous

* Added aditional error handling for mod update background checker ([74f3cc7](https://github.com/Jorixon/JASM/commit/74f3cc77e5a0522a4fdfba712cbb2874fb75aa6c))
* Added some additional error handling when picking 3dmigoto/genshin process ([a940dc7](https://github.com/Jorixon/JASM/commit/a940dc75276bd45d3c9709fb42ea355c527745fb))
* Updated readme and adjusted build settings ([b754ec2](https://github.com/Jorixon/JASM/commit/b754ec28d2b49fbd62bc513930b6457212077095))
* Updated WinAppSDK ([39086ab](https://github.com/Jorixon/JASM/commit/39086ab23bc67f7eadf4b5e39cfcf2f64323644d))

## [2.1.0](https://github.com/Jorixon/JASM/compare/v2.0.0...v2.1.0) (2024-01-08)


### Features

* Now possible to disable all other mods while activating the new mod when installing a new mod ([#116](https://github.com/Jorixon/JASM/issues/116))  ([9130f0c](https://github.com/Jorixon/JASM/commit/9130f0c15b75ac93bc96b0544ab9dfb24960b22e))


### Bug Fixes

* Potential fix for crash when JASM looks for other running instances of itself ([#118](https://github.com/Jorixon/JASM/issues/118)) ([20fafc1](https://github.com/Jorixon/JASM/commit/20fafc1b5480a7df39dce81bd99710da7e8ededd))
* Potential fix for deleting mods freezing the app ([9130f0c](https://github.com/Jorixon/JASM/commit/9130f0c15b75ac93bc96b0544ab9dfb24960b22e))


### Miscellaneous

* Changed restart logic to use winappsdk to restart app. Should hopefully make it more stable ([#114](https://github.com/Jorixon/JASM/issues/114)) ([d7044dd](https://github.com/Jorixon/JASM/commit/d7044ddeb0fdff49762bc2e5ee3bd047c2b9e88e))


### Code Refactoring

* Fixed typo in App Management in folder name / namespace ([d7044dd](https://github.com/Jorixon/JASM/commit/d7044ddeb0fdff49762bc2e5ee3bd047c2b9e88e))
* Redid notifications and updated namespaces ([9130f0c](https://github.com/Jorixon/JASM/commit/9130f0c15b75ac93bc96b0544ab9dfb24960b22e))

## [2.0.0](https://github.com/Jorixon/JASM/compare/v1.9.2...v2.0.0) (2024-01-06)


### ⚠ BREAKING CHANGES

* Redid Folder structure ([#109](https://github.com/Jorixon/JASM/issues/109))

### Features

* Added Mod counter on overview and sort by mod count ([62622a6](https://github.com/Jorixon/JASM/commit/62622a6399dd3d595d6880e2902fdfb2945ee8b2))
* Character/ModObject folders are now created on demand ([62622a6](https://github.com/Jorixon/JASM/commit/62622a6399dd3d595d6880e2902fdfb2945ee8b2))
* Redid Folder structure ([#109](https://github.com/Jorixon/JASM/issues/109)) ([62622a6](https://github.com/Jorixon/JASM/commit/62622a6399dd3d595d6880e2902fdfb2945ee8b2))


### Miscellaneous

* Added "Date Added" to grid in CharacterDetails page ([62622a6](https://github.com/Jorixon/JASM/commit/62622a6399dd3d595d6880e2902fdfb2945ee8b2))
* Added Chevreuse ([#110](https://github.com/Jorixon/JASM/issues/110)) ([4ee26d6](https://github.com/Jorixon/JASM/commit/4ee26d61ad622ba9e508b3c3396e13ffc39c2904))


### Code Refactoring

* Background tasks now use the LongRunning option ([4ee26d6](https://github.com/Jorixon/JASM/commit/4ee26d61ad622ba9e508b3c3396e13ffc39c2904))

## [1.9.2](https://github.com/Jorixon/JASM/compare/v1.9.1...v1.9.2) (2023-12-06)


### Bug Fixes

* Crash window showing on shutdown ([88ab01b](https://github.com/Jorixon/JASM/commit/88ab01b7f52bc9903caae645deaa9a3669a15d9a))

## [1.9.1](https://github.com/Jorixon/JASM/compare/v1.9.0...v1.9.1) (2023-12-02)


### Bug Fixes

* Possible fix for mod folder names containing " ä " or similar characters, causing mod preview image to fail to load ([d184123](https://github.com/Jorixon/JASM/commit/d184123bb158dbd5c805d31818657e5ff7817bbf))


### Tweaks

* Made automatic mod resorting a bit stricter when checking folder name and internal name ([d184123](https://github.com/Jorixon/JASM/commit/d184123bb158dbd5c805d31818657e5ff7817bbf))


### Miscellaneous

* Author now visibly in mod grid ([d184123](https://github.com/Jorixon/JASM/commit/d184123bb158dbd5c805d31818657e5ff7817bbf))
* More npcs and images ([d184123](https://github.com/Jorixon/JASM/commit/d184123bb158dbd5c805d31818657e5ff7817bbf))
* Updated WinAppSDK and a few other packages ([#102](https://github.com/Jorixon/JASM/issues/102)) ([d184123](https://github.com/Jorixon/JASM/commit/d184123bb158dbd5c805d31818657e5ff7817bbf))

## [1.9.0](https://github.com/Jorixon/JASM/compare/v1.8.1...v1.9.0) (2023-11-29)


### Features

* Added Weapons category([#95](https://github.com/Jorixon/JASM/issues/95)) ([6c55bf3](https://github.com/Jorixon/JASM/commit/6c55bf36b1d2492537ff4661b8a76b1d85497547))
* Category support. Added empty objects and minimal npcs categories. ([#93](https://github.com/Jorixon/JASM/issues/93)) ([7349b41](https://github.com/Jorixon/JASM/commit/7349b41347ddc1d9c843afdabed4f71ddfd26035))
* The Elevator process will now automatically refresh mods in game when enabling/disabling mods in JASM ([6c55bf3](https://github.com/Jorixon/JASM/commit/6c55bf36b1d2492537ff4661b8a76b1d85497547))


### Bug Fixes

* Honkai star rail 3DMigotoLoader not starting as admin. Now checking the "run this program as an administrator" on the file "3DMigotoLoader.exe" should start it as admin, this worked for me at least ([7349b41](https://github.com/Jorixon/JASM/commit/7349b41347ddc1d9c843afdabed4f71ddfd26035))


### Tweaks

* Added some more info to the "mod added" notification and "mod moved" notification. ([7349b41](https://github.com/Jorixon/JASM/commit/7349b41347ddc1d9c843afdabed4f71ddfd26035))


### Miscellaneous

* Added more tooltips around the app and some minor text changes ([6c55bf3](https://github.com/Jorixon/JASM/commit/6c55bf36b1d2492537ff4661b8a76b1d85497547))
* Minor improvements to the underlying code of the Mod installer helper ([7349b41](https://github.com/Jorixon/JASM/commit/7349b41347ddc1d9c843afdabed4f71ddfd26035))

## [1.8.1](https://github.com/Jorixon/JASM/compare/v1.8.0...v1.8.1) (2023-11-24)


### Bug Fixes

* JASM crashing on first time startup ([c3048b4](https://github.com/Jorixon/JASM/commit/c3048b40faaf3cd2984884e44fcfd54392f7ee06))

## [1.8.0](https://github.com/Jorixon/JASM/compare/v1.7.0...v1.8.0) (2023-11-24)


### Features

* Mod install helper ([#89](https://github.com/Jorixon/JASM/issues/89)) ([7db7253](https://github.com/Jorixon/JASM/commit/7db725343b9cbe1021ec5984c822d8a7f974a3d8))


### Bug Fixes

* Bug where update notification was connected to character not the mod ([368ef77](https://github.com/Jorixon/JASM/commit/368ef77faa8732a06d0dc80c3470983bc0f0162e))


### Miscellaneous

* Added a simple mods overview page ([ee277e0](https://github.com/Jorixon/JASM/commit/ee277e0ba81bdb2c9fed306b0424f5e4b49505e3))
* Added ModNotifications cleanup ([368ef77](https://github.com/Jorixon/JASM/commit/368ef77faa8732a06d0dc80c3470983bc0f0162e))
* Better handling of invalid jasmConfig, invalid is renamed and new one is created ([7db7253](https://github.com/Jorixon/JASM/commit/7db725343b9cbe1021ec5984c822d8a7f974a3d8))
* More redundant handling of Id in jasm_modconfig ([#88](https://github.com/Jorixon/JASM/issues/88)) ([368ef77](https://github.com/Jorixon/JASM/commit/368ef77faa8732a06d0dc80c3470983bc0f0162e))


### Code Refactoring

* Redid Mod update checker ([#86](https://github.com/Jorixon/JASM/issues/86)) ([ee277e0](https://github.com/Jorixon/JASM/commit/ee277e0ba81bdb2c9fed306b0424f5e4b49505e3))

## [1.7.0](https://github.com/Jorixon/JASM/compare/v1.6.3...v1.7.0) (2023-11-15)


### Features

* Honkai Star Rail support added ([#83](https://github.com/Jorixon/JASM/issues/83)) ([05c4d86](https://github.com/Jorixon/JASM/commit/05c4d862e1e1c70d1b9234dc9c05786314feff8f))


### Bug Fixes

* Unable to restart app when switching game ([a8d59e2](https://github.com/Jorixon/JASM/commit/a8d59e2dd77fa7f115c0f7e62e1d6d1e7978c150))

## [1.6.3](https://github.com/Jorixon/JASM/compare/v1.6.2...v1.6.3) (2023-11-11)


### Bug Fixes

* JASM window being permanently hidden if closed while it was minimized ([ed7fb6c](https://github.com/Jorixon/JASM/commit/ed7fb6ce941c3989f609865fc2ebbc023bf2d0b8))


### Miscellaneous

* JASM will now check if there are other JASM instances running before starting ([ed7fb6c](https://github.com/Jorixon/JASM/commit/ed7fb6ce941c3989f609865fc2ebbc023bf2d0b8))


### Continuous Integration

* Calculate checksum for archive during build ([#81](https://github.com/Jorixon/JASM/issues/81)) ([735d86e](https://github.com/Jorixon/JASM/commit/735d86e19cf5057e8959b8ba3808f38e816368d6))

## [1.6.2](https://github.com/Jorixon/JASM/compare/v1.6.1...v1.6.2) (2023-11-11)


### Bug Fixes

* Automatic reorganization of mods was bugged. This led to (almost) all mods being placed in the "Others" folder ([bb2b0df](https://github.com/Jorixon/JASM/commit/bb2b0dfa6931b2b10e118665287bdeb2f2fdcb93))


### Miscellaneous

* Ability to use mouse 4 and mouse 5 to navigate backward and forward ([d3647d4](https://github.com/Jorixon/JASM/commit/d3647d4b427293fdb2a5420626ab7a2d3f3f4ddd))
* JASM now remembers its last window posistion and if maximized ([aa09b3c](https://github.com/Jorixon/JASM/commit/aa09b3c74f6be157fbd704621440a4da712fe945))

## [1.6.1](https://github.com/Jorixon/JASM/compare/v1.6.0...v1.6.1) (2023-11-10)


### Bug Fixes

* Auto Updater failing, due to being unable to delete WebView2 files ([34d0587](https://github.com/Jorixon/JASM/commit/34d0587c86deb48db566f2c8a78a2856753b2c43))

## [1.6.0](https://github.com/Jorixon/JASM/compare/v1.5.0...v1.6.0) (2023-11-10)


### Features

* JASM will now auto detect image in mod folder, looks for images in this order 1. ".jasm_cover" 2. "preview" 3. "cover" ([f05043c](https://github.com/Jorixon/JASM/commit/f05043c9de954064f5bebc9306e1ca548f9ad496))
* JASM will now check gamebanana urls for new mod files. It does this by checking if there are any new mods since the last check. ([#78](https://github.com/Jorixon/JASM/issues/78)) ([f05043c](https://github.com/Jorixon/JASM/commit/f05043c9de954064f5bebc9306e1ca548f9ad496))


### Bug Fixes

* Unable to search for deactivated characters in the character manager page ([1f3ff34](https://github.com/Jorixon/JASM/commit/1f3ff34010ba345e0b3a3bb323ba3b11cf82deb0))


### Miscellaneous

* Added easter egg because idk ([f05043c](https://github.com/Jorixon/JASM/commit/f05043c9de954064f5bebc9306e1ca548f9ad496))
* Reduced the number of loose files in JASM folder ([f05043c](https://github.com/Jorixon/JASM/commit/f05043c9de954064f5bebc9306e1ca548f9ad496))

## [1.5.0](https://github.com/Jorixon/JASM/compare/v1.4.6...v1.5.0) (2023-10-31)


### Features

* Ability to change display name of characters and disable characters ([#66](https://github.com/Jorixon/JASM/issues/66)) ([691baa9](https://github.com/Jorixon/JASM/commit/691baa9ef1ea750d40815ecad11ee9dee757fab6))


### Miscellaneous

* Auto Updater now checks for windows system folders in the jasm directory before updating ([3fa8758](https://github.com/Jorixon/JASM/commit/3fa875861e7dafc85abcbbbba22de113674ae5b1))
* Laid the foundation for HSR support and localization of game related text like character names ([691baa9](https://github.com/Jorixon/JASM/commit/691baa9ef1ea750d40815ecad11ee9dee757fab6))
* Renamed Travelers to their respective canon names and changed their image ([691baa9](https://github.com/Jorixon/JASM/commit/691baa9ef1ea750d40815ecad11ee9dee757fab6))

## [1.4.6](https://github.com/Jorixon/JASM/compare/v1.4.5...v1.4.6) (2023-10-22)


### Miscellaneous

* Added Wriothesley Character ([6a7943f](https://github.com/Jorixon/JASM/commit/6a7943fe0b5c518cd42a0e61df005f24a77cd694))
* Changed Auto Updater .NET version from 6 to 7 ([ce53022](https://github.com/Jorixon/JASM/commit/ce530223228b3e133218d93a50868775cb2223c2))

## [1.4.5](https://github.com/Jorixon/JASM/compare/v1.4.4...v1.4.5) (2023-10-22)


### Bug Fixes

* KeySwaps not loading when the mod's filepath changed ([#69](https://github.com/Jorixon/JASM/issues/69)) ([b69e24c](https://github.com/Jorixon/JASM/commit/b69e24ce7904ea6070559ec735a71651f74b3dc3))


### Miscellaneous

* Updated WinAppSDK to Version 1.4.2 (1.4.231008000) ([b69e24c](https://github.com/Jorixon/JASM/commit/b69e24ce7904ea6070559ec735a71651f74b3dc3))

## [1.4.4](https://github.com/Jorixon/JASM/compare/v1.4.3...v1.4.4) (2023-10-09)


### Bug Fixes

* JASM will no longer crash if you move 3Dmigoto folder without changing it in the settings ([b334e97](https://github.com/Jorixon/JASM/commit/b334e970eda8ecf9328056186365c5703694a92a))


### Tweaks

* Improved key relevance in character search ([b334e97](https://github.com/Jorixon/JASM/commit/b334e970eda8ecf9328056186365c5703694a92a))


### Miscellaneous

* Ability to disable all mods as a part of first time startup ([b334e97](https://github.com/Jorixon/JASM/commit/b334e970eda8ecf9328056186365c5703694a92a))
* An error window is now shown on crash/exceptions ([b334e97](https://github.com/Jorixon/JASM/commit/b334e970eda8ecf9328056186365c5703694a92a))


### Code Refactoring

* Refactored large parts of the code related to SkinMod ([#63](https://github.com/Jorixon/JASM/issues/63)) ([b334e97](https://github.com/Jorixon/JASM/commit/b334e970eda8ecf9328056186365c5703694a92a))

## [1.4.3](https://github.com/Jorixon/JASM/compare/v1.4.2...v1.4.3) (2023-10-04)


### Bug Fixes

* Multiple mod's active warning shown even if character skin was overridden for the mod ([e52b307](https://github.com/Jorixon/JASM/commit/e52b307bb3963780584bb1621535efe2232aea7f))


### Tweaks

* Added more filtering options to character overview ([fe8dd68](https://github.com/Jorixon/JASM/commit/fe8dd68570265e50053108b0e75edd7dbb04aed1))
* Minor QOL improvements to ModGrid sorting ([#52](https://github.com/Jorixon/JASM/issues/52)) ([fe8dd68](https://github.com/Jorixon/JASM/commit/fe8dd68570265e50053108b0e75edd7dbb04aed1))


### Miscellaneous

* Added JASM auto updater ([#55](https://github.com/Jorixon/JASM/issues/55)) ([e52b307](https://github.com/Jorixon/JASM/commit/e52b307bb3963780584bb1621535efe2232aea7f))
* Added Neuvillette ([c42c7f4](https://github.com/Jorixon/JASM/commit/c42c7f416d9d81731974066e46dab3755d5206bf))
* Added some simplified Chinese translations to Startup page and Settings page. This is mostly a proof of concept and was translated trough chatGPT. Language can be changed on the settings page. ([fe8dd68](https://github.com/Jorixon/JASM/commit/fe8dd68570265e50053108b0e75edd7dbb04aed1))


### Code Refactoring

* Major refactoring of code related to Character Overview sorting and filtering. ([fe8dd68](https://github.com/Jorixon/JASM/commit/fe8dd68570265e50053108b0e75edd7dbb04aed1))

## [1.4.2](https://github.com/Jorixon/JASM/compare/v1.4.1...v1.4.2) (2023-09-30)


### Bug Fixes

* Image failing to load after disabling/enabling mod ([#48](https://github.com/Jorixon/JASM/issues/48)) ([352de20](https://github.com/Jorixon/JASM/commit/352de20a839d4724e621d52ba1dd8ca4df41b3bf))
* Issue where the delete button on the flyout was not clickable if it was infront of the window titlebar ([#50](https://github.com/Jorixon/JASM/issues/50)) ([1fd5495](https://github.com/Jorixon/JASM/commit/1fd54951794d3cdd0dce86bf222c42670d109598))

## [1.4.1](https://github.com/Jorixon/JASM/compare/v1.4.0...v1.4.1) (2023-09-25)


### Miscellaneous

* Added a warning popup if JASM is running with administrator privileges, can be turned off ([#46](https://github.com/Jorixon/JASM/issues/46)) ([93e7a08](https://github.com/Jorixon/JASM/commit/93e7a0850f89b1b049c9d26022c346a7537cc3a9))
* Added missing skin for Klee, Ayaka and Kaeya ([#44](https://github.com/Jorixon/JASM/issues/44)) ([dff8ec0](https://github.com/Jorixon/JASM/commit/dff8ec05861171c252c7c143647e2d3e1bf4821a))
* **Dependencies:** Updated WinAppSDK and WinUIEx ([93e7a08](https://github.com/Jorixon/JASM/commit/93e7a0850f89b1b049c9d26022c346a7537cc3a9))

## [1.4.0](https://github.com/Jorixon/JASM/compare/v1.3.0...v1.4.0) (2023-09-24)


### Features

* Recently added mods are marked with an icon to make it easier to see what mod was just added ([be0947b](https://github.com/Jorixon/JASM/commit/be0947b9cad897da9c123633b0a14664f44793b2))
* Support for handling mods for different ingame skins for characters ([#41](https://github.com/Jorixon/JASM/issues/41)) ([be0947b](https://github.com/Jorixon/JASM/commit/be0947b9cad897da9c123633b0a14664f44793b2))


### Bug Fixes

* Character overview not showing multiple mods active warning when "Only show characters with mods" was enabled ([be0947b](https://github.com/Jorixon/JASM/commit/be0947b9cad897da9c123633b0a14664f44793b2))
* Crash when adding duplicate mod, and better handling of duplicate folder names ([a47fa80](https://github.com/Jorixon/JASM/commit/a47fa8021d94667fbfe557c23e037bf9d497e04b))
* Export progress ring not showing progress if exporting too many mods ([be0947b](https://github.com/Jorixon/JASM/commit/be0947b9cad897da9c123633b0a14664f44793b2))


### Tweaks

* Made the duplicate folder name checker a bit more robust ([be0947b](https://github.com/Jorixon/JASM/commit/be0947b9cad897da9c123633b0a14664f44793b2))
* Reduced number of releases retrieved from GitHub Api when checking for updates ([a47fa80](https://github.com/Jorixon/JASM/commit/a47fa8021d94667fbfe557c23e037bf9d497e04b))


### Miscellaneous

* Bundled 7zip with JASM ([#39](https://github.com/Jorixon/JASM/issues/39)) ([a47fa80](https://github.com/Jorixon/JASM/commit/a47fa8021d94667fbfe557c23e037bf9d497e04b))

## [1.3.0](https://github.com/Jorixon/JASM/compare/v1.2.0...v1.3.0) (2023-09-16)


### Features

* Drag and drop support in character overview ([#35](https://github.com/Jorixon/JASM/issues/35)) ([c443f08](https://github.com/Jorixon/JASM/commit/c443f08b61ce6f9b53e84c64d7c7d4bd7b3ad168))
* Mod Image now has a right click context menu with Paste/Copy/Clear options ([1964f3b](https://github.com/Jorixon/JASM/commit/1964f3b2fcc828d0a4a0afca2497a37ef0db8ec6))
* Possible to add a back key or forward key if it was missing from merged.ini ([46710a3](https://github.com/Jorixon/JASM/commit/46710a3fd13df1852e2168b4ef10c8d4660b3e34))
* Possible to set custom name for mods. ([#32](https://github.com/Jorixon/JASM/issues/32)) ([1964f3b](https://github.com/Jorixon/JASM/commit/1964f3b2fcc828d0a4a0afca2497a37ef0db8ec6))


### Bug Fixes

* Unsetting all keys for a character removes the key section row in JASM ([#30](https://github.com/Jorixon/JASM/issues/30)) ([46710a3](https://github.com/Jorixon/JASM/commit/46710a3fd13df1852e2168b4ef10c8d4660b3e34))


### Tweaks

* On the Delete mods confirmation popup, the Delete button is now the primary button. So pressing Enter will immediately delete the mods, while pressing space will toggle the Recycle checkbox ([c443f08](https://github.com/Jorixon/JASM/commit/c443f08b61ce6f9b53e84c64d7c7d4bd7b3ad168))


### Miscellaneous

* Added Weapons as its own character ([3c906cd](https://github.com/Jorixon/JASM/commit/3c906cdae2aa9e26ecff7279c332469141a0907f))
* Better error message for when "Run as administrator" property is set on 3DMigoto exe ([c443f08](https://github.com/Jorixon/JASM/commit/c443f08b61ce6f9b53e84c64d7c7d4bd7b3ad168))
* Delete key can be used to delete selected mods in ([c443f08](https://github.com/Jorixon/JASM/commit/c443f08b61ce6f9b53e84c64d7c7d4bd7b3ad168))
* The current path is now shown as a tooltip for Genshin- and 3DMigoto launch buttons ([c443f08](https://github.com/Jorixon/JASM/commit/c443f08b61ce6f9b53e84c64d7c7d4bd7b3ad168))

## [1.2.0](https://github.com/Jorixon/JASM/compare/v1.1.1...v1.2.0) (2023-09-11)


### Continuous Integration

* Better release pipeline ([abc3c1d](https://github.com/Jorixon/JASM/commit/abc3c1da409cb5fa885fe7e1dfefdf80398d9f44))
* Simple characters.json tests and automatic builds ([4bfa960](https://github.com/Jorixon/JASM/commit/4bfa9608610f19f01483a87a66e93384bca59707))


### Miscellaneous

* Added Freminet ([0ff9ad1](https://github.com/Jorixon/JASM/commit/0ff9ad1339e8b8d2a198cb6148c0f6d99160670c))
* Added Gamebanana shortcut to Character overview for easy access ([0ff9ad1](https://github.com/Jorixon/JASM/commit/0ff9ad1339e8b8d2a198cb6148c0f6d99160670c))
* Improved Startup screen text ([0ff9ad1](https://github.com/Jorixon/JASM/commit/0ff9ad1339e8b8d2a198cb6148c0f6d99160670c))


### Features

* Ability to customize merged.ini keys and add link to mod ([#22](https://github.com/Jorixon/JASM/issues/22)) ([c3485cd](https://github.com/Jorixon/JASM/commit/c3485cd562a901268835c1ef6600c63c23b7700b))
* Export Mods function to export all mods managed by JASM to a specified folder ([0ff9ad1](https://github.com/Jorixon/JASM/commit/0ff9ad1339e8b8d2a198cb6148c0f6d99160670c))
* Mod thumbnail that can be added to mod via drag and drop or file selector ([0ff9ad1](https://github.com/Jorixon/JASM/commit/0ff9ad1339e8b8d2a198cb6148c0f6d99160670c))
* Warning ' ! ' icon shown on Character overview when multiple mods are active for character ([0ff9ad1](https://github.com/Jorixon/JASM/commit/0ff9ad1339e8b8d2a198cb6148c0f6d99160670c))


### Bug Fixes

* Temporary folder cleanup on application exit ([0ff9ad1](https://github.com/Jorixon/JASM/commit/0ff9ad1339e8b8d2a198cb6148c0f6d99160670c))


### Tweaks

* Improved character search, especially for characters with longer names ([#19](https://github.com/Jorixon/JASM/issues/19)) ([00b7914](https://github.com/Jorixon/JASM/commit/00b79145c7db0b591229ec010235d3990eda533b))

## [1.1.1](https://github.com/Jorixon/JASM/compare/v1.1.0...v1.1.1) (2023-09-04)


### Bug Fixes

* Zhongli,Navia and Paimon,Yun Jin having duplicate ids ([#16](https://github.com/Jorixon/JASM/issues/16)) ([5740b05](https://github.com/Jorixon/JASM/commit/5740b05c1ec412b5500908b6028c6e876e9d360c))

## [1.1.0](https://github.com/Jorixon/JASM/compare/v1.0.0...v1.1.0) (2023-09-03)


### Features

* Added Paimon, Gliders and some characters from Fontaine. ([b6ceb06](https://github.com/Jorixon/JASM/commit/b6ceb06b93148724c28dccc559d25c84a5dd4e51))
* Added Paimon, Gliders and some characters from Fontaine. ([#13](https://github.com/Jorixon/JASM/issues/13)) ([b6ceb06](https://github.com/Jorixon/JASM/commit/b6ceb06b93148724c28dccc559d25c84a5dd4e51))
* Qol, when selected character for moving mods the move button will recieve focus ([b6ceb06](https://github.com/Jorixon/JASM/commit/b6ceb06b93148724c28dccc559d25c84a5dd4e51))
* Small badge shown when a new JASM release is available ([#10](https://github.com/Jorixon/JASM/issues/10)) ([69eb509](https://github.com/Jorixon/JASM/commit/69eb5098e36c121e248e7240fab423bcc223831a))


### Bug Fixes

* Better description of reorganize mods ([b6ceb06](https://github.com/Jorixon/JASM/commit/b6ceb06b93148724c28dccc559d25c84a5dd4e51))
* Closing JASM will now NOT close Migoto or Genshin if they were started trough it.... ([b6ceb06](https://github.com/Jorixon/JASM/commit/b6ceb06b93148724c28dccc559d25c84a5dd4e51))
* Crash when pressing enter without selecting a charater when moving mods. ([6234d01](https://github.com/Jorixon/JASM/commit/6234d01a8b5a8f53036b879b36b4b21168a7f9b6))
* On character details overview, flyout autmatically focuses on text box on open. ([8a1463e](https://github.com/Jorixon/JASM/commit/8a1463e625227d3afaf71588786d9b92e757e82f))
* please-release test ([fc740a2](https://github.com/Jorixon/JASM/commit/fc740a24886397956e90e199a8ac32544d886e72))
* release pleasev2 ([8a0f08b](https://github.com/Jorixon/JASM/commit/8a0f08bbc80b66ae89d6fb14d6d4e999cfb779e5))
* Removed unecesery code ([d6a68c4](https://github.com/Jorixon/JASM/commit/d6a68c4ada8e8145f3f8f81130afd318a3880277))
* test ([af170ef](https://github.com/Jorixon/JASM/commit/af170ef23e63094550d87baa2b3b4332729523b5))
* That some mod names had an underscore shown  with their name (_ModName) when enabled. ([b6ceb06](https://github.com/Jorixon/JASM/commit/b6ceb06b93148724c28dccc559d25c84a5dd4e51))
* Typo ([ff0edd9](https://github.com/Jorixon/JASM/commit/ff0edd9c858d5757378dd5b5bd5815ec597395c8))
* Typo ([b88fa95](https://github.com/Jorixon/JASM/commit/b88fa95cf70daa14810f9e5034596da37d0aa7d8))
* Typo ([403d269](https://github.com/Jorixon/JASM/commit/403d26960c910624911d9ae8202e92724974582e))
* When navigating to a charcter detailed overview focus is set on grid and not the back button ([b6ceb06](https://github.com/Jorixon/JASM/commit/b6ceb06b93148724c28dccc559d25c84a5dd4e51))
