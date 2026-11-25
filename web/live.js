/* Plate Kinetics Live - the logic behind the shell (web/shell.js): the feed of live curves into the analysis page, the
   texts of the instrument tiles and the numbers of the overview, and what the views of the shell show: experiments,
   problems, notifications, plate layouts, settings. Nothing here touches the DOM, so all of it runs under Node
   (tests/frontend); in the browser it is handed to the shell as window.SAALogic.

   One file for both programs: BMG\web\live.js and Archimed\web\live.js are the same but for the line PROGRAM below,
   and web/shell.js and web/shell.css are the same in both (tests/frontend/test_page_contract.js checks it).

   The page's own code is reached only through window.PKApp, a line the server inserts before the page's boot().

   Languages work as in the page itself: every text is written in English, made with say() when it is made from data
   and marked with en() when it is a fixed label or a text of a table here. ZH holds the Chinese of these words; the
   shell adds it to the page's own Chinese pack (addChinese) and makes say() the page's translator (useWords), so
   every text comes out in the language the page is in. ZH also has the Chinese of the messages of both backends
   (the C# program's Runs.cs and Archimed.cs), which the shell shows through the page's tr(). */
(function () {
'use strict';

/** Which program this copy belongs to: 'bmg' (BMG Omega plate readers) or 'archimed' (the RocGene Archimed PCR). */
const PROGRAM = 'bmg';

/* ---------- words: every text is written in English; ZH is their Chinese, added to the page's own pack ---------- */
const ZH = {
  text: {
    // an instrument and its run (tiles, side bar, lists)
    'Idle. Start a run in the {0} software: its curves appear here by themselves.': '空闲。在 {0} 软件里开始实验后，曲线会自动出现在这里。',
    'Idle': '空闲',
    'Measuring': '测量中',
    'Finished': '已结束',
    'Cannot be read': '无法读取',
    'Ended early': '提前结束',
    'Completed': '已完成',
    'Stopped early': '提前停止',
    'Interrupted': '中断',
    'End not seen': '没看到结束',
    'Reader {0}': '读板仪 {0}',
    'PCR instrument': 'PCR 仪',
    'Experiment': '实验',
    'Channel {0}': '通道 {0}',
    '{0} reading time (s)': '{0} 读数时间 (s)',
    'Instrument': '仪器',
    'Protocol': '方案',
    'Start time': '开始时间',
    'Record ID': '记录编号',
    'Cycle {0}': '第 {0} 轮',
    'Cycle {0} / {1}': '第 {0} / {1} 轮',
    '{0:cycle}': '{0} 轮',
    '{0} / {1} cycles': '{0} / {1} 轮',
    'Cycles done: {0}': '已完成 {0} 轮',
    'Cycles done: {0} / {1}': '已完成 {0} / {1} 轮',
    'Completed {0:cycle}': '共完成 {0} 轮',
    'Completed {0} / {1} cycles': '共完成 {0} / {1} 轮',
    'Only {0} / {1} cycles completed': '只完成 {0} / {1} 轮',
    '{0} / {1} wells read in this cycle': '本轮已读 {0} / {1} 孔',
    'Waiting for the next cycle': '等待下一轮',
    '{0}, waiting for the next cycle': '{0}，等待下一轮',
    'Waiting for cycle 1 to begin': '等待第 1 轮开始',
    'Target {0}': '目标 {0}',
    'Last target {0} °C': '上次目标 {0} °C',
    'in progress, data up to cycle {0} of {1}': '测量中，数据到第 {0}/{1} 轮',
    'in progress, data up to cycle {0}': '测量中，数据到第 {0} 轮',
    // times
    '{0} min': '{0} 分钟',
    '{0} h': '{0} 小时',
    '{0} d': '{0} 天',
    '{0} h {1} min': '{0} 小时 {1} 分',
    '{0} d {1} h': '{0} 天 {1} 小时',
    'under 1 min': '不到 1 分钟',
    'Running for {0}': '已运行 {0}',
    'Took {0}': '历时 {0}',
    'Expected end {0} (about {1} left)': '预计 {0} 结束（还剩约 {1}）',
    'Ended {0}': '结束于 {0}',
    // the curves of a run (the feed)
    'Could not read the curve data: {0}': '读取曲线数据失败：{0}',
    'Curves loaded: {0}': '已载入曲线：{0}',
    'Curves are shown for 96- and 384-well plates only': '曲线只支持 96 孔和 384 孔板',
    'This run has fewer than 2 cycles: no curves can be shown': '这次实验还不满 2 轮，不能显示曲线',
    'Reload': '重新载入',
    'Load the latest curves': '载入最新曲线',
    'Loading the curves…': '正在载入曲线…',
    'Curves loaded: {0} cycles': '曲线已载入 {0} 轮',
    'Curves loaded: {0} cycles (automatic loading is off)': '曲线已载入 {0} 轮（自动载入已关闭）',
    'The curves tab has been closed': '曲线标签页已关闭',
    'Automatic loading is off': '自动载入已关闭',
    'Curves are loaded once 2 cycles are complete': '测满 2 轮后载入曲线',
    'New data: the curves are updated once the mouse button is released': '有新数据，等鼠标松开后刷新曲线',
    'New data: the curves are updated once the dialog is closed': '有新数据，等对话框关闭后刷新曲线',
    'New data: the curves are updated once you have finished typing': '有新数据，等您输入完后刷新曲线',
    'This page cannot show curves': '当前页面不能显示曲线',
    // the shell: fixed labels (the page translates them where they stand)
    'Views': '视图',
    'Overview': '总览',
    'Analysis': '分析',
    'Instruments': '仪器',
    'Data': '数据',
    'Import a data file': '导入数据文件',
    'History': '历史记录',
    'Analysis settings': '分析设置',
    'Threshold, lag time, plateau and the other settings of the algorithms': '阈值、起峰时间、平台期等的算法设置',
    'Show the curves of this instrument': '看这台仪器的曲线',
    'Wells positive': '阳性孔',
    'Look back in this run: drag the dot; at the right end it is live again': '回看这个实验：拖动圆点；拖到最右边就回到实时',
    'Look back in this run': '回看这个实验',
    'Looking back: {0}': '回看 {0}',
    'Groups positive': '阳性分组',
    'Earliest rise': '最早起峰',
    'Open the curves of this run in Analysis': '在分析里打开这次实验的曲线',
    'Open in Analysis': '在分析里打开',
    'Positive': '阳性',
    'No rise': '未起峰',
    'No data': '无数据',
    'View analysis': '查看分析',
    'When this is off, new data of this instrument does not go into the analysis page by itself': '关掉后，这台仪器的新数据不会自动进入分析页面',
    'Load curves automatically': '自动载入曲线',
    'Recent runs': '最近的实验',
    'Last run': '上一次实验',
    'Open': '打开',
    'Close': '关闭',
    'Cancel': '取消',
    // the shell: texts made from data
    'The Plate Kinetics Live background program does not answer': '孔板动力学·实时（Plate Kinetics Live）的后台程序没有响应',
    'The background program refused the request: close the window and start the program again': '后台拒绝了请求，请关闭窗口后重新打开程序',
    'The background program does not have this data': '后台没有这项数据',
    'The request failed ({0})': '请求失败（{0}）',
    // the online view: an experiment's files to download, and what needs a sign-in
    'Download the experiment file (.pk)': '下载实验文件（.pk）',
    'Download the Excel file': '下载 Excel 文件',
    'The file could not be downloaded: {0}': '文件没能下载：{0}',
    'The file did not arrive in full: please try again': '文件没有完整下载下来，请再试一次',
    'Sign in to see this': '登录后才能查看',
    'The request was refused: please reload the page': '请求被拒绝，请重新载入页面',
    'No data yet': '还没有数据',
    '{0:instrument}': '{0} 台仪器',
    '{0} measuring': '{0} 台测量中',
    'No run in progress': '没有实验在运行',
    '{0}; what is shown below is the state before the connection was lost.': '{0}，下面显示的是断开前的状态。',
    '{0} was not found on this PC: only imported data can be analysed.': '这台电脑上没有找到 {0}，只能分析导入的数据。',
    'Live curves need the analysis page that comes with this program: this page cannot load them. The instrument status is shown as usual.': '实时曲线需要本程序自带的分析页面，当前页面不能载入曲线；仪器状态照常可用。',
    '{0} software version {1}': '{0} 软件版本 {1}',
    'Gain {0}': '增益 {0}',
    'Latest data: {0}': '最新数据：{0}',
    'No new data for {0}: please check the instrument and the {1} software.': '已经 {0}没有新数据了，请看一下仪器和 {1} 软件。',
    '+{0} this cycle': '本轮新增 {0} 孔',
    'No groups yet': '还没有分组',
    'No rise yet': '还没有孔起峰',
    'Select replicate wells in “Analysis” and group them (or click “Group by name”): the groups that turned positive are then shown here.': '在“分析”里框选复孔并分组（或点“按名称分组”），这里就会按分组显示哪些转阳了。',
    '{0}: {1} / {2} wells positive': '{0}：{1} / {2} 孔阳性',
    'Unnamed': '未命名',
    'Latest positive groups': '最近转阳的分组',
    'Positive at ({0})': '转阳 {0}',
    'No group has turned positive yet': '还没有分组转阳',
    '{0} more: see all of them in Analysis': '还有 {0} 组，在分析里看全部',
    'All {0} runs': '全部 {0} 条',
    'Loading…': '正在读取…',
    'No run has been recorded yet': '还没有记录过实验',
    // the Experiments folder, Omega's own databases and the data folder (views of the shell, the red bar)
    'Experiments': '实验',
    'All BMG experiments': '全部 BMG 实验',
    'Open data folder': '打开数据文件夹',
    'Show in folder': '在文件夹中显示',
    'Save': '保存',
    'Refresh': '刷新',
    'Data folder': '数据文件夹',
    'Verified with Omega 6.20 and 6.40, plate-mode kinetics, 96 and 384 wells. Runs of other versions or modes are listed too, but they may not open correctly.':
      '已验证：Omega 6.20 和 6.40 的板模式动力学，96 孔和 384 孔板。其他版本或模式的实验也会列出，但不保证能正确打开。',
    '{0:experiment}': '{0} 个实验',
    '{0} wells': '{0} 孔',
    'User {0}': '用户 {0}',
    'No experiment file has been saved yet': '还没有实验文件',
    'No run was found in the Omega databases': '在 Omega 数据库里没有找到实验',
    'No experiment matches the search': '没有符合搜索条件的实验',
    '{0} more: search to find them': '还有 {0} 个，可用搜索找到',
    'No curves were found in {0}': '{0} 里没有找到曲线',
    'Saved: {0}': '已保存：{0}',
    'Saving failed: {0}': '保存失败：{0}',
    'The folder could not be opened: {0}': '无法打开文件夹：{0}',
    'Explorer cannot be opened from here. The location is:': '这里无法打开资源管理器，位置是：',
    'The data could not be saved: please check the disk of the data folder.': '数据无法保存，请检查数据文件夹所在的磁盘。',
    'Only {0} free on the disk of the data folder: new data may not be saved.': '数据文件夹所在的磁盘只剩 {0}，新的数据可能无法保存。',
    // why a run ended early (the words of the C# program's Interruptions.Text)
    'the PC lost power or stopped unexpectedly': '电脑断电或意外停止',
    'the PC restarted for a Windows update': '电脑因 Windows 更新重启',
    'the PC was restarted': '电脑重启',
    'the PC was shut down': '电脑关机',
    'the PC went to sleep': '电脑进入睡眠',
    'the instrument software stopped with an error': '仪器软件出错退出',
    'the instrument software was closed': '仪器软件被关闭',
    'the recording program was stopped': '记录程序被停止',
    'reason unknown': '原因不明',
    '{0} ({1})': '{0}（{1}）',
    'Ended early: {0}': '提前结束：{0}',
    '{0}: {1}': '{0}：{1}',
    // problems, notifications, the side bar
    'Close this message': '关闭这条提示',
    'No new data': '长时间没有新数据',
    'Could not be saved': '没能保存',
    '{0} could not be opened: {1}': '无法打开 {0}：{1}',
    'This PC is kept awake while an experiment is measuring': '测量期间这台电脑不会进入睡眠',
    'Show {0:older experiment}': '显示 {0} 个较早的实验',
    'Hide older experiments': '收起较早的实验',
    'Experiments that ended more than a week ago and were not opened since': '一周前就已结束、之后也没有打开过的实验',
    'Plate layouts': '孔板布局',
    'Settings': '设置',
    'Help': '帮助',
    'About': '关于',
    '{0:layout}': '{0} 个布局',
    'Change': '更改',
    'Next experiment: {0}': '下一个实验：{0}',
    'layout {0}': '布局 {0}',
    // the experiment in the analysis view: its status, operator, Save
    'Operator': '操作人',
    'not given': '未填写',
    'Export current data': '导出当前数据',
    'An Excel file of the data measured so far': '把目前已测到的数据导出为 Excel 文件',
    'Save the names, groups, notes and settings into the experiment file (Ctrl+S)': '把样品名、分组、备注和设置保存到实验文件里（Ctrl+S）',
    'Saving…': '正在保存…',
    'Saved {0}': '已保存 {0}',
    'Saved to the experiment': '已保存到实验',
    'Saved into {0}': '已保存到 {0}',
    'Saved into this program\'s own file of the experiment: {0}': '已保存到本程序自己的实验文件：{0}',
    'Not saved: {0}': '没有保存：{0}',
    'Unsaved changes': '有未保存的改动',
    'This version of the analysis page cannot save into the experiment': '这个版本的分析页面不能保存到实验',
    'This file lies outside the Experiments folder and was not recorded on this PC: it can only be saved into in the program\'s window':
      '这个文件不在 Experiments 文件夹里，也不是这台电脑记录的实验，只能在程序窗口里保存',
    'this experiment was not recorded by this program': '这个实验不是本程序记录的',
    'The window could not save the file': '窗口没能保存这个文件',
    'Only the program\'s window can do this': '只有程序窗口能做这件事',
    'The window did not answer': '窗口没有响应',
    'The saved analysis could not be put back: {0}': '保存的分析没能恢复：{0}',
    'This experiment is open already: {0}': '这个实验已经打开了：{0}',
    'The Excel file was open or had been changed, so a new one was made: {0}': 'Excel 文件正被打开或已被改动，所以另存了一个新文件：{0}',
    'The Excel file could not be made: {0}': '没能生成 Excel 文件：{0}',
    'The Excel file was made again: {0}': 'Excel 文件已重新生成：{0}',
    // the annotation shared with every device that shows the experiment (the status chip, automatic saving)
    'Same as the instrument': '与仪器端一致',
    'Changes only on this device': '有改动只在这台设备上',
    'Newer on the instrument': '仪器端有更新',
    'Restore from instrument': '恢复到仪器端',
    'Undo last save': '退回上一版',
    'Sign in': '登录',
    'Sign in to save them on the instrument PC': '登录后才能保存到仪器电脑',
    'The instrument PC cannot be reached: trying again every 30 s': '暂时连不上仪器电脑，每 30 秒重试一次',
    'The background program does not answer: trying again every 30 s': '后台程序没有响应，每 30 秒重试一次',
    'Drop the changes on this device and load the annotation saved in the experiment': '丢掉这台设备上的改动，载入实验里保存的标注',
    'Bring the annotation back to how it was before the last save': '把标注退回到最后一次保存之前的样子',
    'The annotation goes back to how it was before the last save ({0}).': '标注会退回到最后一次保存（{0}）之前的样子。',
    'The annotation saved in the experiment is shown again': '已恢复为实验里保存的标注',
    'The annotation is back to the version before the last save': '标注已退回上一版',
    'The annotation could not be loaded: {0}': '标注没能载入：{0}',
    'The last save could not be undone: {0}': '没能退回上一版：{0}',
    '{0} saved a newer annotation: it is shown now': '{0} 保存了新的标注，已自动载入',
    // the Experiments view: search, filters, actions
    'Search by name, sample, operator, instrument or date': '按名称、样品、操作人、仪器或日期搜索',
    'Search by name, date or instrument': '按名称、日期或仪器搜索',
    'All instruments': '全部仪器',
    'Started': '开始时间',
    'Any time': '全部日期',
    'Today': '今天',
    'Last 7 days': '最近 7 天',
    'Last 30 days': '最近 30 天',
    'Last 12 months': '最近 12 个月',
    'Status': '状态',
    'Any status': '全部状态',
    'Files with a problem': '有问题的文件',
    'Order': '排序',
    'Newest first': '最新的在前',
    'Oldest first': '最早的在前',
    'By name': '按名称',
    'Analysis saved': '已保存分析',
    'The file is damaged: it cannot be read': '文件已损坏，无法读取',
    'The raw data in this file were changed after the run': '这个文件里的原始数据在实验结束后被改动过',
    'Made by a newer version of this program: please update it to open this file': '这个文件由更新版本的程序生成，请先更新程序再打开',
    'More actions': '更多操作',
    'Rename…': '改名…',
    'Make the Excel file again': '重新生成 Excel 文件',
    'Move to the Recycle Bin…': '移到回收站…',
    'Rename the experiment': '实验改名',
    'New name': '新名称',
    'Rename': '改名',
    'Renamed: {0}': '已改名：{0}',
    'The experiment could not be renamed: {0}': '实验没能改名：{0}',
    'Move to the Recycle Bin': '移到回收站',
    '“{0}” and its Excel file go to the Windows Recycle Bin.': '“{0}”和它的 Excel 文件会移到 Windows 回收站。',
    'The program keeps its own record of the run: the experiment can be made again from the list of deleted experiments.':
      '程序保留着这次实验自己的记录，以后可以在“已删除的实验”里重新生成。',
    'The files could not be moved to the Recycle Bin: {0}': '文件没能移到回收站：{0}',
    'Moved to the Recycle Bin: {0}': '已移到回收站：{0}',
    '{0:deleted experiment} - the program keeps their records': '{0} 个已删除的实验（程序保留了它们的记录）',
    'Restore': '恢复',
    'The experiment files were made again: {0}': '实验文件已重新生成：{0}',
    'The experiment could not be restored: {0}': '实验没能恢复：{0}',
    'The manual could not be opened: {0}': '说明书没能打开：{0}',
    // plate layouts
    'Names and groups of the wells, prepared before a run. Choose one for the next experiment of an instrument: its wells are named when the run starts.':
      '实验之前准备好的孔的样品名和分组。把它设为某台仪器的下一个实验，实验一开始各孔就会按它命名。',
    'New 96-well layout': '新建 96 孔布局',
    'New 384-well layout': '新建 384 孔布局',
    'Import from Excel…': '从 Excel 导入…',
    'No plate layout has been saved yet': '还没有保存过孔板布局',
    '{0} named': '{0} 孔已命名',
    '{0:group}': '{0} 个分组',
    'changed {0}': '修改于 {0}',
    'Next experiment of {0}': '{0} 的下一个实验',
    'Edit': '编辑',
    'Use for the next experiment…': '用于下一个实验…',
    'Delete': '删除',
    'All layouts': '全部布局',
    'Name of the layout': '布局名称',
    'Plate': '孔板',
    'Select all wells': '全选',
    '{0:well} selected': '已选 {0} 孔',
    'Select wells on the plate: drag, or click a row or column': '在孔板上选孔：拖动，或点击行号、列号',
    'Sample name': '样品名称',
    'Group': '分组',
    'Name the wells': '给孔命名',
    'Group the wells': '设为这个分组',
    'Clear the selected wells': '清空选中的孔',
    'Groups of this layout': '这个布局的分组',
    'Tip: drag over the plate to select a block of wells, hold Shift to extend it, Ctrl to add single wells. Delete clears the selected wells.':
      '提示：在孔板上拖动可选中一块孔，按住 Shift 可扩大选区，按住 Ctrl 可逐个加选；按 Delete 清空选中的孔。',
    'Saved': '已保存',
    'Leave the layout': '离开这个布局',
    'The changes to this layout are not saved.': '这个布局的改动还没有保存。',
    'Leave without saving': '不保存，离开',
    'Give the layout a name first': '请先给布局起个名字',
    'Layout saved: {0}': '布局已保存：{0}',
    'The layout could not be saved: {0}': '布局没能保存：{0}',
    'Delete the layout': '删除布局',
    'The layout “{0}” is deleted. Experiments that were named with it keep their names.': '布局“{0}”会被删除。已经用它命名的实验，名字不变。',
    'Deleted: {0}': '已删除：{0}',
    'The layout could not be deleted: {0}': '布局没能删除：{0}',
    'No plate was found in the file': '文件里没有找到孔板',
    'Read from {0}: check the names, then save the layout': '已从 {0} 读入，请核对名称后保存布局',
    'The layout could not be read: {0}': '布局没能读入：{0}',
    'Use for the next experiment': '用于下一个实验',
    'The next run that starts on the instrument gets the names and groups of “{0}”. Once used, the choice is cleared.':
      '这台仪器上下一次开始的实验会用“{0}”的样品名和分组；用过一次后自动取消。',
    'Experiment name': '实验名称',
    'Name of the next experiment': '下一个实验的名称',
    'Name of the next experiment (optional)': '下一个实验的名称（可不填）',
    'Use this layout': '使用这个布局',
    'Set for the next experiment of {0}': '已设为 {0} 的下一个实验',
    // Settings
    'The settings could not be read: {0}': '设置没能读取：{0}',
    'The settings could not be saved: {0}': '设置没能保存：{0}',
    'Settings saved': '设置已保存',
    'The settings have unsaved changes': '设置有未保存的改动',
    'Discard changes': '放弃改动',
    'Save settings': '保存设置',
    'The name on screen is shown on the tiles, in the lists and in the notifications; file names keep the model and serial number. The next experiment of an instrument gets the name and the plate layout chosen here (once).':
      '显示名称用在卡片、列表和通知里；文件名仍用型号和序列号。仪器的下一个实验会用这里设好的名称和孔板布局（只用一次）。',
    'Name on screen': '显示名称',
    'Next experiment': '下一个实验',
    'Name (optional)': '名称（可不填）',
    'Plate layout for the next experiment': '下一个实验的孔板布局',
    'No layout': '不用布局',
    'Notifications': '通知',
    'Windows notifications come from the program\'s icon in the taskbar, also when the window is closed. The window shows them too, without a sound.':
      'Windows 通知由任务栏里的程序图标发出，窗口关着也会提醒；窗口里也会显示，但不响铃。',
    'The window shows a notification when:': '以下情况时，窗口里显示通知：',
    'An experiment is completed': '实验完成',
    'An experiment stopped early': '实验提前停止',
    'An experiment was interrupted': '实验中断',
    'No new data for a long time': '长时间没有新数据',
    'Data could not be saved': '数据没能保存',
    'Play a sound': '响铃提示',
    'Keep the PC awake': '防止电脑睡眠',
    'Keep this PC awake while an experiment is measuring': '实验测量期间不让这台电脑进入睡眠',
    'Active now: an experiment is measuring.': '现在生效：有实验正在测量。',
    'Not needed now: no experiment is measuring.': '现在不需要：没有实验在测量。',
    'Windows may otherwise go to sleep during a long run; the instrument software then stops reading.': '否则长时间的实验中 Windows 可能进入睡眠，仪器软件就会停止读数。',
    'Default operator': '默认操作人',
    'the Windows user': 'Windows 用户',
    'Recorded with every new experiment. Leave it empty to record the Windows user who is logged on. It can be changed for each experiment in the analysis.':
      '每个新实验都会记下操作人。留空则记下当前登录的 Windows 用户。每个实验都可以在分析页面里单独修改。',
    'Free space': '剩余空间',
    'Back up the whole data folder. Experiments holds your files; System is the program\'s own (records, logs, settings).':
      '备份时请备份整个数据文件夹。Experiments 里是您的实验文件；System 是程序自己用的（记录、日志、设置）。',
    'Checks of this PC': '电脑检查',
    'The checks could not be read.': '检查结果没能读取。',
    'Check again': '重新检查',
    'Program versions': '版本信息',
    'Program': '程序',
    'Edition': '版本类型',
    'Analysis page': '分析页面',
    'Installed, with the background service': '安装版（带后台服务）',
    'Portable': '便携版',
    'Support': '技术支持',
    'A file for whoever helps you: the program\'s logs, versions, settings and checks. It holds no experiment data unless you tick the box. Nothing is sent anywhere: you pass the file on yourself.':
      '给帮您排查问题的人的文件：程序的日志、版本、设置和检查结果。不勾选就不含实验数据。不会自动发送到任何地方，由您自己转交。',
    'Include the experiment files (larger; holds your data)': '包括实验文件（文件较大，含您的数据）',
    'Save a support file…': '保存支持文件…',
    'The support file could not be made: {0}': '支持文件没能生成：{0}',
    // About
    'For research use only, not for diagnostic use.': '仅供科研，不用于临床诊断。',
    'Licence': '许可',
    'MIT License. Free to use, copy, change and pass on, also in other laboratories, provided the copyright and licence notice is kept.':
      'MIT 许可。可以免费使用、复制、修改和转给其他实验室，但须保留版权和许可声明。',
    'The program is provided as is, without warranty of any kind. The results of an analysis are the user\'s to check.': '本程序按“原样”提供，不作任何担保。分析结果需由使用者自行核对。',
    'Instrument makers': '仪器厂商',
    'This program reads the files of the instrument software; it does not control the instruments. It is not made, endorsed or supported by BMG LABTECH or RocGene. Omega and MARS are trademarks of BMG LABTECH; Archimed is a trademark of RocGene.':
      '本程序只读取仪器软件的文件，不控制仪器。它不是 BMG LABTECH 或 RocGene 公司制作的，也未经它们认可或支持。Omega 和 MARS 是 BMG LABTECH 的商标；Archimed 是 RocGene 的商标。',
    'Privacy': '隐私',
    'The program works on this PC only. It sends nothing anywhere: no data, no usage figures, no updates are fetched. Its page is served on this PC and opens only in its own window.':
      '本程序只在这台电脑上工作，不向任何地方发送任何东西：不发数据，不发使用统计，也不联网检查更新。它的页面只在这台电脑上提供，只在程序自己的窗口里打开。',
    'Third-party software': '第三方软件',
    'reads the workbooks in the analysis page': '分析页面用它读取表格文件',
    'the window of the installed and portable editions (Microsoft)': '安装版和便携版的窗口（Microsoft）',
    'part of Windows': 'Windows 自带',
    'The analysis page on the web': '网页版分析页面',
    'The same analysis, without the instruments, for files from anywhere:': '同样的分析功能，不接仪器，可以分析任何地方来的文件：',
    // messages of the program without variable parts (Runs.cs, Archimed.cs)
    'The run has started; waiting for the data of cycle 1.': '实验已开始，等待第 1 轮数据。',
    'Omega is not running: this is the data the last measurement left behind.': 'Omega 没有在运行，这是上次测量留下的数据。',
    'Archimed software is not running: these are the last data it recorded.': 'Archimed 软件没有在运行，这是它最后记录下的数据。',
    'The run has started. Archimed reads the plate at the end of every cycle: waiting for the first reading.':
      '实验已开始，Archimed 每一轮结束时读一次板，等第 1 次读数。',
    'PCR run': 'PCR 实验',
    // the protocol details of a run (what the instrument software recorded about how it was set up)
    'Protocol details': '方案详情',
    'The settings of this experiment, as the instrument software recorded them': '仪器软件记录的这次实验的设置',
    'No record': '没有记录',
    'Temperature program': '温度程序',
    'Stage {0}': '阶段 {0}',
    '{0} (data collected)': '{0} (采光)',
    '{0}, ramp {1}': '{0}，速率 {1}',
    'Ramp rate {0} for every step': '每一步的升降温速率都是 {0}'
  },
  // messages of the backends with variable parts: {0} any text, {#0} a number, {~0} a message that is translated too
  patterns: [
    ['Reading failed: {0}', '读取出错：{0}'],
    ['Cannot open {0}: {1}', '无法打开 {0}：{1}'],
    ['The log folder of Archimed Analyzer was not found ({0}). Make sure that Archimed Analyzer is installed on this PC and has been run once.',
      '没有找到 Archimed Analyzer 的日志文件夹（{0}）。请确认这台电脑装了 Archimed Analyzer 并运行过一次。']
  ]
};
/** A text in English: {0} is a value, {0:word} a count with its noun ("1 cycle", "2 cycles"), as in the page's own T(). */
let say = (fmt, ...args) => fmt.replace(/\{(\d+)(?::([^}]+))?\}/g, (_, i, noun) => {
  const v = args[i] == null ? '' : String(args[i]);
  return noun ? v + ' ' + (v.replace(/,/g, '') === '1' ? noun : noun + 's') : v;
});
/** A fixed label or a text of a table: written in English, shown in the page's language by the page's own translator
    (or by say() where it is used to make a text). */
const en = text => text;
/** The translator the texts are made with: English by default; the shell sets the page's own (app.L). */
function useWords(fn) { if (typeof fn === 'function') say = fn; }
/** Adds the Chinese of these words to the page's Chinese pack (C = window.PKCore). Call before boot(); a pack in use
    is read again. Returns whether there was a Chinese pack. */
function addChinese(C) {
  const zh = C && C.LOCALES && C.LOCALES.zh;
  if (!zh || typeof zh !== 'object') return false;
  if (!zh.text || typeof zh.text !== 'object') zh.text = {};
  for (const word in ZH.text) if (!Object.prototype.hasOwnProperty.call(zh.text, word)) zh.text[word] = ZH.text[word];
  if (!Array.isArray(zh.patterns)) zh.patterns = [];
  const known = new Set(zh.patterns.map(p => p && p[0]));
  for (const p of ZH.patterns) if (!known.has(p[0])) zh.patterns.push(p.slice());
  if (typeof C.language === 'function' && typeof C.setLanguage === 'function' && C.language() !== 'en') C.setLanguage(C.language());
  return true;
}
/** The Chinese of a text of this file (for the few texts shown in both languages at once), or the text itself. */
const chineseOf = text => (Object.prototype.hasOwnProperty.call(ZH.text, text) ? ZH.text[text] : text);

/* ---------- the two programs ---------- */
const PROGRAMS = {
  bmg: { id: 'bmg', name: 'Plate Kinetics Live', software: 'Omega', instruments: 'BMG Omega', vendor: 'BMG LABTECH' },
  archimed: { id: 'archimed', name: 'Plate Kinetics Live (Archimed)', software: 'Archimed', instruments: 'RocGene Archimed', vendor: 'RocGene' }
};
const ME = PROGRAMS[PROGRAM] || PROGRAMS.bmg;
/** The program an instrument belongs to, by its key ("Omega2" a BMG reader, "Archimed" the PCR); this program's own
    for any other key. */
const kindOf = key => (/^Archimed/i.test(key || '') ? PROGRAMS.archimed : /^Omega/i.test(key || '') ? PROGRAMS.bmg : ME);
/** The software that runs an instrument: what the server says (inst.software), else the program's. */
const softwareOf = inst => (inst && inst.software) || kindOf(inst && inst.key).software;

/* ===================== logic without a DOM (tested in tests/frontend) ===================== */

const TYPING_IDLE_MS = 15000;      // a focused field counts as "typing" only this long after the last key or click
const STATES = { idle: en('Idle'), measuring: en('Measuring'), finished: en('Finished'), unreadable: en('Cannot be read') };
// how an experiment stands (the "status" of the server: /api/state runs, /api/experiments, the .pk)
const STATUS = { measuring: en('Measuring'), completed: en('Completed'), stopped: en('Stopped early'), interrupted: en('Interrupted'), unknown: en('End not seen') };
// why new curves wait: the whole sentence for each reason
const HOLD = { pointer: en('New data: the curves are updated once the mouse button is released'), dialog: en('New data: the curves are updated once the dialog is closed'),
  typing: en('New data: the curves are updated once you have finished typing') };
const NOT_DRAWN = en('Curves are shown for 96- and 384-well plates only');
const LOADED = en('Curves loaded: {0} cycles');                  // the plain line of describe() that a tile does not repeat
const WAITING_NEXT = en('Waiting for the next cycle');
// names the programs themselves give a run: Archimed.cs Unnamed, for a run whose experiment file was not found
const OWN_NAMES = [en('PCR run')];

const fin = Number.isFinite;
const isObj = v => !!v && typeof v === 'object' && !Array.isArray(v);
const plateOk = p => !!p && ((p.rows === 8 && p.cols === 12) || (p.rows === 16 && p.cols === 24));     // all the page can draw
const datasetId = (runId, channelIndex) => (channelIndex ? runId + '-ch' + (channelIndex + 1) : runId);
const numbers = (values, n, scale) => Float64Array.from({ length: n }, (_, i) => (fin(values[i]) ? values[i] * scale : NaN));
const hours = (seconds, n) => numbers(seconds, n, 1 / 3600);       // the page keeps time in hours
const has = (table, key) => Object.prototype.hasOwnProperty.call(table, key);

/** How many leading cycles of a run can be drawn: completely measured, with a usable and increasing time. */
function usableCycles(run) {
  const s = run.seconds || [], max = Math.min(run.cyclesComplete || 0, s.length);
  let n = 0;
  while (n < max && fin(s[n]) && (n === 0 || s[n] > s[n - 1])) n++;
  return n;
}

/* ---------- names of the instruments: the screen names of the settings, else a number or the kind ---------- */
let screenNames = {};
/** The screen names of the settings ({ key: { displayName } }); shown instead of "Reader 1" and the like. */
function setScreenNames(instruments) {
  screenNames = {};
  if (isObj(instruments)) for (const key in instruments) {
    const name = isObj(instruments[key]) && typeof instruments[key].displayName === 'string' ? instruments[key].displayName.trim() : '';
    if (name) screenNames[key] = name;
  }
}
/** "Omega" -> { n: 1, name: 'Reader 1' }, "Omega3" -> 3: how the user counts the readers; "Archimed" -> 'P', the PCR
    instrument. Other keys keep their name. A screen name given in the settings is the name. */
function reader(key) {
  const own = has(screenNames, key || '') ? screenNames[key] : '';
  if (/^Archimed/i.test(key || '')) return { n: 'P', name: own || say('PCR instrument') };
  // the online view's page for several PCR PCs names their instruments PCR1, PCR2 ... (online/server MergedArea)
  const pcr = /^PCR(\d{1,2})$/.exec(key || '');
  if (pcr) return { n: 'P' + pcr[1], name: own || 'PCR ' + pcr[1] };
  const m = /^Omega\s*\(?(\d*)\)?$/i.exec(key || '');
  const n = m ? +m[1] || 1 : 0;
  return { n, name: own || (n ? say('Reader {0}', n) : key || '') };
}
/** The instrument's name in every language the program has: a dataset keeps the name it was given, in the language
    that was in use then (the title must not repeat "Reader 1" beside a dataset whose name has it in Chinese). */
function readerNames(key) {
  const own = has(screenNames, key || '') ? [screenNames[key]] : [];
  if (/^Archimed/i.test(key || '')) return own.concat(['PCR instrument', ZH.text['PCR instrument']]);
  if (/^PCR\d{1,2}$/.test(key || '')) return own.concat([reader(key).name]);
  const n = reader(key).n;
  return own.concat(n ? ['Reader {0}', ZH.text['Reader {0}']].map(f => f.replace('{0}', n)) : [key || '']);
}
/** The name of a run as shown: its protocol's or experiment file's own name as it is (user data), a name a program
    gave it in the language in use, "Experiment" for none. */
const runName = name => (!name ? say('Experiment') : OWN_NAMES.includes(name) ? say(name) : String(name));

/** info = { label, protocol, startedAt, excitation, emission, name }: what the instrument list or the history says about
    a run; `name` (an experiment of the Experiments folder, a run of Omega's database) names the dataset as it is.
    The name is made in the language in use: it belongs to the dataset from then on, like every name on the page. */
function datasetName(run, channelIndex, info) {
  const when = (info.startedAt || '').replace('T', ' ').slice(5, 16);
  const name = info.name || [runName(info.protocol), reader(run.key).name, when].filter(Boolean).join(' · ');
  return run.channels.length > 1 ? name + ' · ' + say('Channel {0}', run.channels[channelIndex].channel) : name;
}
/** The name of the dataset of an experiment opened from its file: the experiment's name, with the instrument and the
    start when the name does not hold them (a file name of an older version does: "2026-01-15 10-30-00 alice - ..."). */
function experimentDatasetName(e, key) {
  const name = String((e && e.name) || '');
  if (/^\d{4}-\d\d-\d\d/.test(name)) return name;
  const when = String((e && e.startedAt) || '').replace('T', ' ').slice(5, 16);
  return [name || say('Experiment'), key ? reader(key).name : instrumentText(e && e.instrument), when].filter(Boolean).join(' · ');
}

/** The input of the page's newDataset() for one channel of a run, cut to its first n cycles. */
function datasetInput(run, channelIndex, n, info) {
  const ch = run.channels[channelIndex], names = {}, kind = kindOf(run.key);
  const series = ch.wells.map(w => {
    if (w.content) names[w.id] = w.content;
    return { id: w.id, y: numbers(w.y, n, 1), ovf: 0 };
  });
  const filters = run.channels.length !== 1 ? '' : info.excitation && info.emission ? info.excitation + '/' + info.emission : info.excitation || '';
  // when the run started (its times count from there): the page can splice it to another dataset by actual time. As
  // the clock of the instrument PC read it, a zone the text may carry left out: every start the page compares is such
  // a clock time (a run's "Start time" line, a table's dates), so a viewer in another time zone sees the same pause.
  const startMs = wallMs(info.startedAt), timeInfo = fin(startMs) ? { unit: 's', sourceKind: 'time', startMs } : { unit: 's', sourceKind: 'time' };
  return {
    t: hours(run.seconds, n), series, names, plate: { rows: run.plate.rows, cols: run.plate.cols },
    channel: filters || say('Channel {0}', ch.channel), warnings: [], timeInfo,
    // printed in the page's export notes; made in the language in use, like the page's own notes about a source
    sourceInfo: {
      timeLabel: say('{0} reading time (s)', kind.software), native: { format: kind.instruments + ' · Plate Kinetics Live', notes: [] },
      groups: [[say('Instrument'), info.label || run.key], [say('Protocol'), info.protocol ? runName(info.protocol) : ''],
        [say('Start time'), (info.startedAt || '').replace('T', ' ').slice(0, 19)], [say('Record ID'), run.id]].map(([label, value]) => ({ label, value }))
    }
  };
}
/** What the page's exports say about the state of a run's data while it is measured ('' once it has ended). */
function inProgressText(cycles, planned, measuring) {
  if (!measuring) return '';
  return planned > 0 ? say('in progress, data up to cycle {0} of {1}', cycles, planned) : say('in progress, data up to cycle {0}', cycles);
}

/** What to do with one channel of a run. n = usable cycles; have = time points already in its dataset, -1 when it
    has no dataset; closed = that dataset was on the page earlier in this session (tab closed, project opened over it). */
function planChannel(plate, n, have, opt) {
  if (!plateOk(plate)) return 'unsupported';
  // the same number of cycles with other values: data taken too early is replaced (opt.changed)
  if (have >= 0) return (opt.auto || opt.manual) && (n > have || (n === have && n > 0 && !!opt.changed)) ? 'append' : 'none';
  if (n < 2) return 'wait';
  return opt.manual || (opt.auto && !opt.closed) ? 'create' : opt.closed ? 'closed' : 'off';
}

/** Whether the first n cycles of a channel of a run differ from what a dataset holds (times or any reading). */
function differs(ds, run, channelIndex, n) {
  const same = (a, b) => a === b || (Number.isNaN(a) && Number.isNaN(b));
  const t = hours(run.seconds, n);
  for (let k = 0; k < n; k++) if (!same(ds.t[k], t[k])) return true;
  for (const w of run.channels[channelIndex].wells) {
    const x = ds.wells[w.id];
    if (!x || !x.y) continue;
    const y = numbers(w.y, n, 1);
    for (let k = 0; k < n; k++) if (!same(x.y[k], y[k])) return true;
  }
  return false;
}

/** A field in which typing would be lost if the page redrew it. */
function isEntry(el) {
  if (!el || !el.tagName) return false;
  const tag = el.tagName.toUpperCase();
  return (tag === 'INPUT' && !/^(button|checkbox|radio|range|color|file|submit|reset)$/i.test(el.type || '')) || tag === 'TEXTAREA' || tag === 'SELECT' || !!el.isContentEditable;
}
/** Why the page must not be redrawn right now ('' = go ahead). ui = { pointerDown, overlay, entry, idleMs }. */
function holdReason(ui) {
  if (ui.pointerDown) return 'pointer';
  if (ui.overlay) return 'dialog';
  return ui.entry && ui.idleMs < TYPING_IDLE_MS ? 'typing' : '';
}

/**
 * Keeps the page's datasets in step with the runs: one dataset per run and channel, found again by id on every
 * step, never by a kept reference. env = { app: window.PKApp, fetchRun(key, id) -> Promise<run>, auto(key) -> bool,
 * busy() -> hold reason, toast(text), changed(), redraw(...parts) (optional: instead of app.inv for the dataset in view),
 * created(ds, { runId, key, channel, run }) (optional: a dataset of a run was just put on the page),
 * protocol(runId, protocolDetails, datasetRunId) (optional: a fetched run said how it was set up) }.
 */
function createFeed(env) {
  const app = env.app;
  const jobs = new Map();      // run id -> what is known about the run and what is under way for it
  const seen = new Set();      // every dataset id that was on the page in this session
  let tick = 0;                // counts the state polls: at most one automatic fetch per run and poll
  const find = id => app.S.ds.find(d => d.id === id);
  const redraw = env.redraw || app.inv;          // the shell keeps the place in the results table over a redraw

  function job(key, id) {
    if (!jobs.has(id)) jobs.set(id, { key, id, info: {}, live: false, measuring: false, planned: 0, manual: false, loading: false, payload: null, fetched: -1, rev: null, asked: -1, error: '', held: '', settled: null, fetchedSettled: null });
    return jobs.get(id);
  }
  /** Takes the instrument list of /api/state. */
  function track(instruments) {
    const live = new Set();
    tick++;
    for (const inst of instruments) {
      const r = inst.run;
      if (!r) continue;
      live.add(r.id);
      Object.assign(job(inst.key, r.id), {
        live: true, measuring: inst.state === 'measuring', planned: r.plannedCycles || 0, cyclesComplete: r.cyclesComplete, dataRev: r.dataRev, plate: r.plate,
        settled: r.settled == null ? null : r.settled,
        info: { label: inst.label, protocol: r.protocol, startedAt: r.startedAt, excitation: r.excitation, emission: r.emission }
      });
    }
    for (const j of jobs.values()) if (!live.has(j.id)) { j.live = false; j.measuring = false; }
  }
  /** The user asked for this run's curves: Reload, Load the latest curves, or a recorded run from the history.
      source(key, id) -> Promise<run>: where the run comes from this once instead of env.fetchRun (a .pk file of the
      Experiments folder, a run of Omega's database, a file the program's window was asked to open). */
  function load(key, id, info, shown, source) {
    const j = job(key, id);
    if (info) j.info = info;
    j.manual = true;
    j.shown = shown || null;            // called once the run's dataset is in front (never for one that cannot be shown)
    j.source = source || null;
  }
  function fetchRun(j) {
    const get = j.source || env.fetchRun;
    j.source = null;
    j.loading = true;
    j.asked = tick;
    Promise.resolve().then(() => get(j.key, j.id)).then(run => {
      if (!run || typeof run !== 'object' || !Array.isArray(run.channels)) throw new Error(say('No curves were found in {0}', j.id));
      j.fetched = run.cyclesComplete || 0; j.rev = run.dataRev; j.fetchedSettled = run.settled == null ? null : run.settled; j.payload = run; j.error = '';
      // how the run was set up, when the program says it (a run of an older program does not): for the shell to show
      if (env.protocol && run.protocolDetails !== undefined) { try { env.protocol(String(run.id || j.id), run.protocolDetails, j.id); } catch (e) { /* only shown */ } }
    })
      .catch(e => {
        if (j.manual) env.toast(say('Could not read the curve data: {0}', e.message));      // a run from the history has no card to say it on
        j.error = e.message; j.manual = false; j.shown = null;
      })
      .then(() => { j.loading = false; env.changed(); });
  }
  function show(index) { const S = app.S; S.cur = index; S.zoom = null; S.sort = { key: null, dir: 1 }; }
  /** What the exports of the page say about the data of a run while it is measured. */
  function provenance(ds, j, n) {
    if (!ds.sourceInfo || typeof ds.sourceInfo !== 'object') return;
    const text = inProgressText(n, j.planned, j.live && j.measuring);
    if (text) ds.sourceInfo.provenance = text; else delete ds.sourceInfo.provenance;
  }
  function create(j, run, i, n) {
    const S = app.S, before = app.curDS();
    const ds = app.newDataset(datasetInput(run, i, n, j.info), { name: datasetName(run, i, j.info), file: j.info.label || run.key });
    ds.id = datasetId(j.id, i);
    provenance(ds, j, n);
    // an untouched example makes way, as it does for an import (the page's rule: steps set from the chart's lines are no
    // edit, notes are)
    S.ds = S.ds.filter(d => !(d.isDemo && !d.notes && !d.undo.some(s => !String(s).startsWith('{"k":"set"'))));
    S.ds.push(ds);
    seen.add(ds.id);
    const at = S.ds.indexOf(before);
    if (at >= 0) S.cur = at; else show(S.ds.length - 1);            // what the user has open stays open
    app.invAll();
    env.toast(say('Curves loaded: {0}', ds.name));
    if (env.created) { try { env.created(ds, { runId: j.id, key: j.key, channel: i, run }); } catch (e) { /* the shell's part: the dataset is there */ } }
  }
  function append(ds, j, run, i, n) {
    // always NEW arrays: the page's analysis cache and its autosave both go by array identity
    ds.t = hours(run.seconds, n);
    for (const w of run.channels[i].wells) { const x = ds.wells[w.id]; if (x) x.y = numbers(w.y, n, 1); }
    provenance(ds, j, n);
    ds.ver++;
    if (ds === app.curDS()) redraw('tabs', 'notice', 'plate', 'groups', 'chart', 'results'); else app.inv('tabs');
  }
  /** Puts a fetched run into the page. Returns true when that has to wait for the user. */
  function apply(j) {
    const run = j.payload, n = usableCycles(run), hold = env.busy();
    const auto = j.live && env.auto(j.key);
    let held = false;
    run.channels.forEach((ch, i) => {
      const id = datasetId(j.id, i), ds = find(id), have = ds ? ds.t.length : -1;
      const changed = !!ds && n > 0 && n === have && differs(ds, run, i, n);
      const plan = planChannel(run.plate, n, have, { auto, manual: j.manual, closed: seen.has(id), changed });
      if (plan !== 'create' && plan !== 'append') return;
      if (hold) held = true;
      else if (plan === 'create') create(j, run, i, n);
      else append(ds, j, run, i, n);
    });
    j.held = held ? hold : '';
    if (held) return true;
    if (j.manual) {                               // asked for by hand: say why nothing came, or bring the tab to the front
      const at = app.S.ds.indexOf(find(j.id));
      if (!plateOk(run.plate)) env.toast(say(NOT_DRAWN));
      else if (at < 0) env.toast(say('This run has fewer than 2 cycles: no curves can be shown'));
      else { if (at !== app.S.cur) { show(at); app.invAll(); } if (j.shown) j.shown(); }
    }
    j.payload = null; j.manual = false; j.shown = null;
    return false;
  }
  /** Fetches what is needed and applies what has arrived. Returns true when it should be called again soon. */
  function step() {
    // the page is still reading its saved session back (restorePending: it shows itself already and takes the session
    // when it comes): feeding now would lose that session
    if (app.S.restoring || app.S.restorePending) return true;
    for (const d of app.S.ds) seen.add(d.id);
    let again = false;
    for (const j of jobs.values()) {
      if (!j.loading && !j.payload) {
        const auto = j.live && env.auto(j.key) && plateOk(j.plate) && j.cyclesComplete >= 2 && !(seen.has(j.id) && !find(j.id));
        // dataRev moves with every well that is read; a fetch is only worth it once another cycle is complete, or
        // once the values of complete cycles have changed (`settled`; a server without it: complete cycles only)
        const stale = j.fetched < j.cyclesComplete || (j.settled != null && j.settled !== j.fetchedSettled);
        if (j.manual || (auto && j.asked !== tick && j.rev !== j.dataRev && stale)) fetchRun(j);
      }
      if (j.payload) again = apply(j) || again;
      if (!j.measuring) {                           // a run that has ended: its exports no longer call it in progress
        for (let i = 0, ds; (ds = find(datasetId(j.id, i))); i++) if (ds.sourceInfo && ds.sourceInfo.provenance) delete ds.sourceInfo.provenance;
      }
      if (!j.live && !j.manual && !j.loading && !j.payload) jobs.delete(j.id);
    }
    return again;
  }
  /** For the instrument card: what to say about a live run's curves and which button to offer ('' = none). */
  function describe(id) {
    const j = jobs.get(id);
    if (!j) return { text: '', button: '' };
    if (!plateOk(j.plate)) return { text: say(NOT_DRAWN), button: '' };
    const ds = find(id), auto = env.auto(j.key), closed = !ds && seen.has(id), enough = j.cyclesComplete >= 2;
    const button = closed || j.error ? say('Reload') : !auto && enough && (!ds || ds.t.length < j.cyclesComplete) ? say('Load the latest curves') : '';
    let text;
    if (j.error) text = say('Could not read the curve data: {0}', j.error);
    else if (j.held) text = say(HOLD[j.held] || HOLD.typing);
    else if (j.loading || j.payload) text = say('Loading the curves…');
    else if (ds) text = say(auto ? LOADED : 'Curves loaded: {0} cycles (automatic loading is off)', ds.t.length);
    else if (closed) text = say('The curves tab has been closed');
    else if (!auto) text = say('Automatic loading is off');
    else text = say(enough ? 'Loading the curves…' : 'Curves are loaded once 2 cycles are complete');
    return { text, button };
  }
  return { track, load, step, describe };
}

/* ---------- texts of the instrument card ---------- */
/** A run that ended short of its plan: as the server says (stopped, interrupted, end not seen), or - from a server
    that does not say - at least two cycles short (one short may just be a last cycle that was not caught). */
function endedEarly(inst) {
  if (inst.state !== 'finished' || !inst.run) return false;
  const status = inst.run.status;
  if (status === 'completed') return false;
  if (status === 'stopped' || status === 'interrupted' || status === 'unknown') return true;
  return inst.run.plannedCycles - inst.run.cyclesComplete >= 2;
}
/** "Cycle 37 / 300": the cycle a run is in ("Cycle 37" without a plan). */
const cycleText = (n, planned) => (planned ? say('Cycle {0} / {1}', n, planned) : say('Cycle {0}', n));
/** "98 / 300 cycles": the cycles a run has ("98 cycles" without a plan). */
const cyclesText = (n, planned) => (planned ? say('{0} / {1} cycles', n, planned) : say('{0:cycle}', n));
/** The cycles of a recorded run in a list (r = an item of /api/runs; instruments = those of /api/state):
    "Measuring · 28 / 200 cycles" while it is measured, "98 / 300 cycles" when fewer than planned, "300 cycles"
    otherwise. A record does not tell whether a run was stopped or only not followed to its end (this program was
    closed meanwhile), so it is not called "ended early" here; the tile of a run that this program saw end says so. */
function recordCycles(r, instruments) {
  if (!r || r.cycles == null) return '';
  const live = (instruments || []).some(i => i.state === 'measuring' && i.run && i.run.id === r.id && i.key === r.key);
  const of = cyclesText(r.cycles, r.plannedCycles > r.cycles ? r.plannedCycles : 0);
  return live ? say(STATES.measuring) + ' · ' + of : of;
}
/** The state of an instrument in a word: how its run ended once the server says so (Completed, Stopped early,
    Interrupted, End not seen), else Idle, Measuring, Finished, Ended early, Cannot be read. */
function stateText(inst) {
  const status = inst.run && inst.run.status;
  if (inst.state === 'finished' && status && status !== 'measuring' && has(STATUS, status)) return say(STATUS[status]);
  return endedEarly(inst) ? say('Ended early') : has(STATES, inst.state) ? say(STATES[inst.state]) : inst.state || '';
}
/** Where a run stands, in two parts: { main: "Cycle 37 / 300", note: "32 / 108 wells read in this cycle" }. */
function progressParts(inst) {
  const r = inst.run;
  if (!r) return { main: '', note: '' };
  const planned = r.plannedCycles;
  if (inst.state !== 'measuring' && inst.state !== 'finished') {      // cannot be read now: whether it has ended is not known
    return { main: say(planned ? 'Cycles done: {0} / {1}' : 'Cycles done: {0}', r.cyclesComplete, planned), note: '' };
  }
  if (inst.state !== 'measuring') {
    return { main: planned ? say(endedEarly(inst) ? 'Only {0} / {1} cycles completed' : 'Completed {0} / {1} cycles', r.cyclesComplete, planned) : say('Completed {0:cycle}', r.cyclesComplete), note: '' };
  }
  if (r.cyclesStarted > r.cyclesComplete) return { main: cycleText(r.cyclesStarted, planned), note: say('{0} / {1} wells read in this cycle', r.wellsInCurrentCycle, r.wells) };
  return r.cyclesComplete ? { main: say(planned ? 'Cycles done: {0} / {1}' : 'Cycles done: {0}', r.cyclesComplete, planned), note: say(WAITING_NEXT) }
    : { main: say('Waiting for cycle 1 to begin'), note: '' };
}
function progressText(inst) {
  const p = progressParts(inst);
  if (!p.note) return p.main;
  return p.note === say(WAITING_NEXT) ? say('{0}, waiting for the next cycle', p.main) : p.main + ' · ' + p.note;
}
/** Which parts of a tile show. loaded = the run's dataset is on the page. An instrument that has begun a run whose
    first cycle is not in yet is "measuring" without a run: it is not idle, and there is nothing to draw yet. */
function tileParts(inst, loaded) {
  const run = !!inst.run;
  return { run, idle: !run && inst.state === 'idle', chart: run, numbers: run && !!loaded, open: run && !!loaded, auto: run };
}
/** Whether a text is `fmt` (with one number for {0}) as say() makes it, whatever the number. */
function isCount(fmt, text) {
  const [head, tail] = say(fmt, '\u0000').split('\u0000');
  if (tail === undefined || typeof text !== 'string' || text.length <= head.length + tail.length) return false;
  return text.startsWith(head) && text.endsWith(tail) && /^\d+$/.test(text.slice(head.length, text.length - tail.length));
}
/** What a tile says about the curves of its run. d = the feed's describe(run id): { text, button }.
    -> { empty: text in place of the chart, note: a line under the numbers, button: its label or '' }.
    With the curves on the page the plain "Curves loaded: N cycles" is not repeated, everything else is said. */
function tileLive(d, loaded, hasFeed) {
  if (!hasFeed) return { empty: loaded ? '' : say('This page cannot show curves'), note: '', button: '' };
  if (!loaded) return { empty: d.text, note: '', button: d.button };
  return { empty: '', note: isCount(LOADED, d.text) ? '' : d.text, button: d.button };
}
/** The temperature line of a tile: the newest reading of the run, with the run's own target when it has one (known
    once Omega has saved the run; Archimed sends none), never that of an earlier run; without a run, the target that
    was set last, when the server knows it. */
function temperatureText(inst) {
  const run = inst.run;
  if (run && fin(run.temperature)) return run.temperature.toFixed(1) + ' °C' + (run.targetTemperature != null ? ' · ' + say('Target {0}', run.targetTemperature) : '');
  return inst.targetTemperature != null ? say('Last target {0} °C', inst.targetTemperature) : '';
}

/* ---------- the overview: time, progress and what the analysis says about a run right now ---------- */
const pad2 = n => String(n).padStart(2, '0');
/** '2026-01-16T09:00:00' (the instrument PC's own clock) -> milliseconds; NaN when it is not a time. A time with its
    zone ("+08:00", "Z": the experiment files, the events) is that moment. */
function stampMs(text) {
  const s = String(text || ''), m = /^(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d)(?::(\d\d))?/.exec(s);
  if (!m) return NaN;
  if (/(?:Z|[+-]\d\d:?\d\d)$/i.test(s.trim())) { const at = Date.parse(s.trim()); if (fin(at)) return at; }
  return new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6] || 0).getTime();
}
/** '2026-01-16T09:00:00' or '2026-01-16T09:00:00+08:00' -> the clock time it reads, in this browser's zone
    (milliseconds; NaN when it is not a time): a zone is left out. */
function wallMs(text) {
  const m = /^(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d)(?::(\d\d))?/.exec(String(text || ''));
  return m ? new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6] || 0).getTime() : NaN;
}
/** A moment as the programs write it: the local time with its offset, "2026-01-20T09:30:00+08:00". */
function localStamp(ms) {
  const d = new Date(ms), off = -d.getTimezoneOffset(), a = Math.abs(off);
  return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + 'T' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds())
    + (off < 0 ? '-' : '+') + pad2(Math.floor(a / 60)) + ':' + pad2(a % 60);
}
/** "11-19 14:30", or "14:30" when that is today. */
function clockText(ms, nowMs) {
  if (!fin(ms)) return '—';
  const d = new Date(ms), now = new Date(nowMs), time = pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  const sameDay = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth() && d.getDate() === now.getDate();
  return sameDay ? time : pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + ' ' + time;
}
/** A length of time the way one says it: "3 d 2 h", "9 h 15 min", "12 min", "under 1 min". */
function spanText(seconds) {
  if (!(seconds >= 0)) return '—';
  const m = Math.floor(seconds / 60), h = Math.floor(m / 60), d = Math.floor(h / 24);
  if (d) return h % 24 ? say('{0} d {1} h', d, h % 24) : say('{0} d', d);
  if (h) return m % 60 ? say('{0} h {1} min', h, m % 60) : say('{0} h', h);
  return m ? say('{0} min', m) : say('under 1 min');
}
/** Where a run stands in time. run = the run of /api/state; measuring = it is still going.
    -> { fraction 0..1 or NaN, elapsed s, remaining s or NaN, endsAt ms or NaN }.
    The end is the one the instrument's software expects (`endsAt`: Archimed logs the time left) when it is known.
    Otherwise it follows the instrument's timetable: cycle k begins (k - 1) cycle times after the start (Omega's log of a
    real run shows the readings 900 s apart to the second, the first one right at the start), so the last reading
    begins at start + (planned - 1) cycles and the run is over when that plate is read. Counting "what is left times a
    cycle" from now instead was up to a whole cycle late and jumped back at every reading. Only an instrument that is
    behind that timetable (paused) is counted from now. */
function runTiming(run, measuring, nowMs) {
  const planned = run.plannedCycles || 0, done = Math.min(run.cyclesComplete || 0, planned || Infinity);
  const started = stampMs(run.startedAt), last = stampMs(run.updatedAt), cycle = run.cycleSeconds > 0 ? run.cycleSeconds : NaN;
  const until = measuring ? nowMs : last;
  let endsAt = NaN;
  if (measuring && fin(stampMs(run.endsAt))) endsAt = Math.max(stampMs(run.endsAt), nowMs);          // the instrument's software knows it
  else if (measuring && planned && fin(cycle)) {
    const begun = Math.max(run.cyclesStarted || 0, done), due = fin(started) ? Math.min(planned, Math.floor((nowMs - started) / 1000 / cycle) + 1) : NaN;
    endsAt = fin(due) && begun >= due - 1 ? started + (planned - 1) * cycle * 1000 : nowMs + Math.max(0, planned - done) * cycle * 1000;
    endsAt = Math.max(endsAt, nowMs);
  }
  return {
    fraction: planned ? done / planned : NaN,
    elapsed: fin(started) ? Math.max(0, (until - started) / 1000) : NaN,
    remaining: fin(endsAt) ? (endsAt - nowMs) / 1000 : NaN,
    endsAt
  };
}
/** How long a run of `cycles` readings `cycleSeconds` apart takes: from the first reading to the last (the reading of
    the plate itself, under a minute for 108 wells, comes on top). 0 for a single reading. */
const runSeconds = (cycles, cycleSeconds) => (cycles > 1 && cycleSeconds > 0 ? (cycles - 1) * cycleSeconds : 0);

/** "Running for 9 h 15 min · Expected end 11-19 14:30 (about 2 d 17 h left)" / "Took 40 h · Ended 11-17 02:12". */
function timesText(inst, nowMs) {
  const run = inst.run, measuring = inst.state === 'measuring';
  if (!run) return '';
  const t = runTiming(run, measuring, nowMs), ended = stampMs(run.updatedAt);
  if (!measuring && inst.state !== 'finished') return fin(ended) ? say('Latest data: {0}', clockText(ended, nowMs)) : '';
  return [fin(t.elapsed) ? say(measuring ? 'Running for {0}' : 'Took {0}', spanText(t.elapsed)) : '',
    measuring && fin(t.endsAt) ? say('Expected end {0} (about {1} left)', clockText(t.endsAt, nowMs), spanText(t.remaining)) : '',
    !measuring && fin(ended) ? say('Ended {0}', clockText(ended, nowMs)) : ''].filter(Boolean).join(' · ');
}

/** For a run that is being measured: the seconds since its last data when that is more than two cycles (and
    more than five minutes) ago, else 0. An instrument that hangs, or software that waits for an answer, shows so. */
function quietSeconds(run, nowMs) {
  const last = stampMs(run.updatedAt);
  if (!fin(last)) return 0;
  const quiet = (nowMs - last) / 1000, limit = Math.max(300, 2 * (run.cycleSeconds || 0) + 60);
  return quiet > limit ? Math.floor(quiet) : 0;
}

/** What the page's analysis says about a dataset, for a tile and for the list of samples.
    ds = the page's dataset; res = its analysis (well id -> result of analyzeWell); grpPosPct = the share of positive
    replicates that makes a group positive; colour(spec) -> '#rrggbb' or null. Times are hours.
    -> { wells, positive, positiveIds, firstTime, groups: [{ id, name, color, wells, positive, call, first, time }], groupsPositive };
    a group's `time` is when it became positive: the rise of the replicate that made the call. */
function runSummary(ds, res, grpPosPct, colour, at) {
  // at (hours): the run as it was then - a well counts as positive once it had crossed its threshold (null: now)
  const wells = ds.order.map(id => ds.wells[id]).filter(w => w && w.y && !w.excluded && res[w.id] && res[w.id].n > 0);
  const isPos = w => !!res[w.id].positive && (at == null || (fin(res[w.id].ttt) && res[w.id].ttt <= at + 1e-9));
  const time = w => { const r = res[w.id]; return fin(r.lag) ? r.lag : fin(r.ttt) ? r.ttt : NaN; };
  const earliest = ws => ws.filter(isPos).map(time).filter(fin).reduce((a, b) => Math.min(a, b), Infinity);
  const positive = wells.filter(isPos);
  const groups = (ds.groups || []).map(g => {
    const ws = wells.filter(w => w.group === g.id), times = ws.filter(isPos).map(time).filter(fin).sort((a, b) => a - b);
    const pos = ws.filter(isPos).length, call = ws.length > 0 && pos / ws.length * 100 >= grpPosPct;
    const need = Math.max(1, Math.ceil(grpPosPct * ws.length / 100 - 1e-9));       // positive wells that make the group positive
    return { id: g.id, name: g.name || '', color: (colour && colour(g.color)) || null, wells: ws.length, positive: pos, call,
      first: times.length ? times[0] : NaN, time: call && times.length >= need ? times[need - 1] : NaN };
  }).filter(g => g.wells);
  const first = earliest(wells);
  return { wells: wells.length, positive: positive.length, positiveIds: positive.map(w => w.id), firstTime: fin(first) ? first : NaN,
    groups, groupsPositive: groups.filter(g => g.call).length };
}
/** The wells that are positive now and were not before (ids). before = null: nothing is new (first look at a run). */
function newlyPositive(before, now) {
  if (!before) return [];
  const known = new Set(before);
  return now.filter(id => !known.has(id));
}
/** "1.66 h" / "99.6 min": a time in hours shown in the unit the page is set to ('h' or 'min'). */
function timeText(hoursValue, unit) {
  if (!fin(hoursValue)) return '—';
  const v = unit === 'min' ? hoursValue * 60 : hoursValue;
  return (v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)) + (unit === 'min' ? ' min' : ' h');
}

/* Looking back in a run from the overview: a dot on the progress bar of a tile. The bar stands for the planned
   cycles (for those read when the plan is not known); the dot stands at a reading of the run's curves, at the newest
   one while the tile is live. */
/** The moment at f (0..1 along the bar): the time (hours) of the reading there, or null at the newest one (live). */
function lookBackAt(ds, f, planned) {
  const n = ds && ds.t ? ds.t.length : 0;
  if (n < 2) return null;
  const of = Math.max(n, planned > 0 ? planned : 0), c = Math.round(Math.min(1, Math.max(0, fin(f) ? f : 1)) * of);
  return c >= n ? null : ds.t[Math.max(1, c) - 1];
}
/** Where the dot stands (0..1 along the bar) for the moment `at` (hours; null: the newest reading). */
function lookBackPlace(ds, at, planned) {
  const n = ds && ds.t ? ds.t.length : 0, of = Math.max(n, planned > 0 ? planned : 0);
  if (!of) return 0;
  if (at == null) return n / of;
  let i = 0;
  while (i < n && ds.t[i] <= at + 1e-9) i++;
  return Math.max(1, i) / of;
}
/** `delta` readings on from the moment `at` (null: the newest reading); past the newest one it is live (null). */
function lookBackStep(ds, at, delta) {
  const n = ds && ds.t ? ds.t.length : 0;
  if (n < 2) return null;
  let i = n;
  if (at != null) { i = 0; while (i < n && ds.t[i] <= at + 1e-9) i++; i = Math.max(1, i); }
  const j = Math.min(n, Math.max(1, i + delta));
  return j >= n ? null : ds.t[j - 1];
}

/** The curves of a dataset for a small chart: every well with data, the positive ones last (drawn on top), thinned
    to about `points` time points. colour(well) -> '#rrggbb' or null (a well or group colour chosen on the page).
    -> { t: number[], tMax, yMin, yMax, curves: [{ id, y: number[], positive, color }] } */
function miniCurves(ds, res, points, colour, at) {          // at (hours): positive as runSummary says (null: now)
  const n = ds.t.length, step = Math.max(1, Math.ceil(n / Math.max(2, points)));
  const index = [];
  for (let i = 0; i < n; i += step) index.push(i);
  if (n && index[index.length - 1] !== n - 1) index.push(n - 1);              // the newest point is always there
  let yMin = Infinity, yMax = -Infinity;
  const curves = ds.order.map(id => ds.wells[id]).filter(w => w && w.y && !w.excluded).map(w => {
    const y = index.map(i => (fin(w.y[i]) ? w.y[i] : NaN));
    for (const v of y) if (fin(v)) { if (v < yMin) yMin = v; if (v > yMax) yMax = v; }
    const r = res && res[w.id];
    return { id: w.id, y, positive: !!(r && r.positive && (at == null || (fin(r.ttt) && r.ttt <= at + 1e-9))), color: (colour && colour(w)) || null };
  });
  curves.sort((a, b) => a.positive - b.positive);
  if (!(yMax > yMin)) { yMin = fin(yMin) ? yMin - 1 : 0; yMax = yMin + 2; }
  const t = index.map(i => ds.t[i]);
  return { t, tMax: t.length ? t[t.length - 1] : 0, yMin, yMax, curves };
}
/** Round figures for an axis from 0..max: [0, step, 2*step, ...] with at most `count` steps above 0. */
function axisSteps(max, count) {
  if (!(max > 0)) return [0];
  const raw = max / Math.max(1, count), pow = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 2.5, 5, 10].map(f => f * pow).find(v => v >= raw - 1e-12 * raw);
  const out = [];
  for (let i = 0; i * step <= max * (1 + 1e-9); i++) out.push(+(i * step).toPrecision(12));
  return out;
}
/** 98500 -> "98.5K": a signal value in little room. */
function shortNumber(v) {
  if (!fin(v)) return '';
  const a = Math.abs(v);
  if (a >= 1e6) return +(v / 1e6).toPrecision(3) + 'M';
  if (a >= 1e3) return +(v / 1e3).toPrecision(3) + 'K';
  return String(+v.toPrecision(3));
}

/* ---------- why a run ended early (the server looks it up in Windows' event logs around the gap) ---------- */
const REASONS = {
  'power-loss-or-crash': en('the PC lost power or stopped unexpectedly'),
  'windows-update-restart': en('the PC restarted for a Windows update'),
  restart: en('the PC was restarted'),
  shutdown: en('the PC was shut down'),
  sleep: en('the PC went to sleep'),
  'instrument-software-crashed': en('the instrument software stopped with an error'),
  'instrument-software-closed': en('the instrument software was closed'),
  'recorder-stopped': en('the recording program was stopped'),
  unknown: en('reason unknown')
};
/** "03:10" (the clock of the PC, as the time is written), "11-26 03:10" when it is another day than `startedAt`'s. */
function whenOf(at, startedAt) {
  const m = /^(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d)/.exec(String(at || ''));
  if (!m) return '';
  const day = /^(\d{4}-\d\d-\d\d)/.exec(String(startedAt || ''));
  return (day && day[1] !== m[1] + '-' + m[2] + '-' + m[3] ? m[2] + '-' + m[3] + ' ' : '') + m[4] + ':' + m[5];
}
/** An interruption { at, reason, detail } in words: "03:10 the PC restarted for a Windows update (by TrustedInstaller)". */
function interruptionText(interruption, startedAt) {
  if (!isObj(interruption)) return '';
  const reason = say(has(REASONS, interruption.reason) ? REASONS[interruption.reason] : REASONS.unknown);
  const detail = typeof interruption.detail === 'string' ? interruption.detail.trim() : '';
  const why = detail ? say('{0} ({1})', reason, detail) : reason;
  const when = whenOf(interruption.at, startedAt);
  return when ? when + ' ' + why : why;
}
/** The line a card shows for a run that was interrupted: "Ended early: 03:10 the PC restarted for a Windows update". */
const interruptionLine = run => (run && isObj(run.interruption) ? say('Ended early: {0}', interruptionText(run.interruption, run.startedAt)) : '');

/* ---------- the Experiments folder, Omega's own databases and the data folder (views of the shell) ---------- */
/** The words of a search in lower case; a date may be typed with / or . instead of - (2026/11/15). */
function searchWords(query) {
  return String(query || '').toLowerCase().replace(/(\d)[/.](?=\d)/g, '$1-').split(/\s+/).filter(Boolean);
}
/** Whether every word of a search is found in one of the texts of a row. */
function found(words, texts) {
  const all = texts.filter(t => t != null && t !== '').join('\n').toLowerCase();
  return words.every(w => all.includes(w));
}
/** An instrument as a list gives it: "Omega 0415-001234" as it is, or { model, serial }. */
const instrumentText = v => (v && typeof v === 'object' ? [v.model, v.serial].filter(Boolean).join(' ') : v == null ? '' : String(v));
const whenText = stamp => String(stamp || '').replace('T', ' ').slice(0, 16);
const newestFirst = (a, b) => String(b.startedAt || '').localeCompare(String(a.startedAt || '')) || String(b.name || '').localeCompare(String(a.name || ''));
/** The status of an experiment in a word (Measuring, Completed, Stopped early, Interrupted, End not seen). */
function statusText(status) {
  return status && has(STATUS, status) ? say(STATUS[status]) : status ? String(status) : '';
}
/** GET /api/experiments as the shell keeps it: { items, removed } (an older program answers the items alone). */
function experimentList(answer) {
  const items = Array.isArray(answer) ? answer : isObj(answer) && Array.isArray(answer.items) ? answer.items : [];
  const removed = isObj(answer) && Array.isArray(answer.removed) ? answer.removed : [];
  return { items: items.filter(isObj), removed: removed.filter(isObj) };
}
// what is wrong with an experiment file ("problem" of /api/experiments)
const PROBLEMS = { damaged: en('The file is damaged: it cannot be read'), changed: en('The raw data in this file were changed after the run'),
  newer: en('Made by a newer version of this program: please update it to open this file') };
const experimentProblem = e => (e && e.problem ? say(has(PROBLEMS, e.problem) ? PROBLEMS[e.problem] : String(e.problem)) : '');
/** The status in the language in use: the server's own words ({ en, zh }: "Stopped early at cycle 98 of 300") when it
    has them, else the word and the cycles. */
function experimentStatus(e, zh) {
  const own = e && isObj(e.statusText) ? (zh ? e.statusText.zh || e.statusText.en : e.statusText.en || e.statusText.zh) : '';
  const c = e && Array.isArray(e.cycles) ? e.cycles : [], done = c[0], planned = c[1];
  const cycles = fin(done) ? cyclesText(done, planned > done ? planned : 0) : '';
  if (own) return e.status === 'unknown' && cycles ? own + ' · ' + cycles : String(own);
  return [statusText(e && e.status), cycles].filter(Boolean).join(' · ');
}
const DAYS = { today: 0, week: 7, month: 30, year: 365 };
/** The experiments of the list that a search and the filters find. filter = { instrument ('' = all), date ('', 'today',
    'week', 'month', 'year'), status ('', a status, or 'problem'), sort ('newest', 'oldest', 'name') }. */
function experimentRows(list, query, filter, nowMs) {
  const words = searchWords(query), f = filter || {}, now = fin(nowMs) ? nowMs : Date.now();
  let since = -Infinity;
  if (f.date && has(DAYS, f.date)) { const d = new Date(now); d.setHours(0, 0, 0, 0); since = d.getTime() - DAYS[f.date] * 86400e3; }
  const rows = (Array.isArray(list) ? list : []).filter(e => isObj(e)
    && (!f.instrument || instrumentText(e.instrument) === f.instrument)
    && (!f.status || (f.status === 'problem' ? !!e.problem : e.status === f.status))
    && (since === -Infinity || stampMs(e.startedAt) >= since)
    && found(words, [e.name, e.file, whenText(e.startedAt), e.startedAt, instrumentText(e.instrument), statusText(e.status),
      isObj(e.statusText) ? e.statusText.en : '', isObj(e.statusText) ? e.statusText.zh : '', e.operator, e.samples, e.runId]));
  if (f.sort === 'name') return rows.sort((a, b) => String(a.name || '').localeCompare(String(b.name || ''), undefined, { numeric: true }) || newestFirst(a, b));
  rows.sort(newestFirst);
  return f.sort === 'oldest' ? rows.reverse() : rows;
}
/** What a row of the Experiments list says under the name:
    "2026-01-15 10:30 · Omega 0415-001234 · Completed · 300 cycles · alice". */
function experimentMeta(e, zh) {
  return [whenText(e.startedAt), instrumentText(e.instrument), experimentStatus(e, zh), e.operator ? String(e.operator) : ''].filter(Boolean).join(' · ');
}
/** The instruments the experiments were measured on (for the filter), in order. */
function instrumentsOf(list) {
  return [...new Set((Array.isArray(list) ? list : []).map(e => instrumentText(isObj(e) ? e.instrument : '')).filter(Boolean))].sort();
}
/** The experiments whose files were deleted (restorable) that a search finds, newest first. */
function removedRows(list, query) {
  const words = searchWords(query);
  return (Array.isArray(list) ? list : []).filter(r => isObj(r) && found(words, [r.name, whenText(r.startedAt), instrumentText(r.instrument), r.runId])).sort(newestFirst);
}
/** The runs of Omega's own databases (GET /api/bmg/history: { key, install, user, testName, id1, startedAt, reader,
    serial, wells, cycles }) that a search finds, newest first. */
function omegaRows(list, query) {
  const words = searchWords(query);
  return (Array.isArray(list) ? list : []).filter(r => r && typeof r === 'object'
    && found(words, [r.id1, r.testName, whenText(r.startedAt), r.startedAt, r.reader, r.serial, r.user, r.install])).sort(newestFirst);
}
/** The name of a run of Omega's database: the ID1 typed in Omega, else its protocol. */
const omegaTitle = r => String(r.id1 || r.testName || '') || say('Experiment');
/** "2026-01-15 10:30 · FLUOstar Omega 415-0012 · 384 wells · 40 cycles · User alice" (and the protocol when ID1 names the run). */
function omegaMeta(r) {
  const wells = Number(r.wells), cycles = r.cycles == null || r.cycles === '' ? NaN : Number(r.cycles);
  return [whenText(r.startedAt), r.id1 && r.testName && r.id1 !== r.testName ? r.testName : '', instrumentText({ model: r.reader, serial: r.serial }),
    wells > 0 ? say('{0} wells', wells) : '', fin(cycles) ? say('{0:cycle}', cycles) : '', r.user ? say('User {0}', r.user) : ''].filter(Boolean).join(' · ');
}
/** The name of the dataset of a run of Omega's database: "RT-QuIC 3 · FLUOstar Omega 415-0012 · 01-15 10:30". */
const omegaName = r => [omegaTitle(r), instrumentText({ model: r.reader, serial: r.serial }), whenText(r.startedAt).slice(5)].filter(Boolean).join(' · ');

/** A run that comes whole - the run.json of a .pk file (the body of /api/run, or a record of an older version:
    { summary, data, label, instrument }), the body of /api/run as Omega's database gives it, or either as a JSON text -
    as the feed's load() takes it: { run, info }; null when it holds no curves. extra = what a list knows ({ name,
    label, protocol, startedAt }); a name given there names the dataset. */
function runOf(json, extra) {
  let j = json;
  if (typeof j === 'string') { try { j = JSON.parse(j); } catch (e) { return null; } }
  if (!j || typeof j !== 'object') return null;
  const data = j.data && typeof j.data === 'object' && Array.isArray(j.data.channels) ? j.data : j;
  if (!Array.isArray(data.channels) || !data.channels.length || !data.plate) return null;
  const sum = j.summary && typeof j.summary === 'object' ? j.summary : {}, x = extra || {};
  const pick = (...values) => { for (const v of values) if (v != null && v !== '') return String(v); return ''; };
  const info = { label: pick(x.label, j.label, instrumentText(j.instrument), data.key), protocol: pick(x.protocol, sum.protocol, data.protocol),
    startedAt: pick(x.startedAt, sum.startedAt, data.startedAt), excitation: pick(sum.excitation, data.excitation), emission: pick(sum.emission, data.emission) };
  if (x.name) info.name = String(x.name);
  // a run that does not say how many cycles are complete (a run of a database): all that have a time (it has ended)
  const complete = fin(data.cyclesComplete) ? data.cyclesComplete : Array.isArray(data.seconds) ? data.seconds.length : 0;
  const run = Object.assign({}, data, { key: pick(data.key, j.instrument && j.instrument.key), id: pick(data.id, sum.id), cyclesComplete: complete });
  const layout = j.omegaLayout || data.omegaLayout || sum.omegaLayout;
  if (layout) run.omegaLayout = layout;
  // how it was set up (protocolDetails: the program's record has it in its summary, a run of /api/run in itself)
  const details = [data.protocolDetails, sum.protocolDetails, j.protocolDetails].find(v => v !== undefined);
  if (details !== undefined) run.protocolDetails = details;
  return { run, info };
}
/** An id for a dataset that the page keeps over a restart (letters, digits, - and _; with a channel's "-chN" at most
    128). An id that is one already stays as it is, so a run gets the same dataset from wherever it is opened. */
function safeId(text) {
  const s = String(text == null ? '' : text);
  if (/^[A-Za-z0-9_-]{1,100}$/.test(s)) return s;
  let hash = 2166136261;
  for (let i = 0; i < s.length; i++) { hash ^= s.charCodeAt(i); hash = Math.imul(hash, 16777619) >>> 0; }
  return (s.replace(/[^A-Za-z0-9_-]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 80) || 'run') + '-' + hash.toString(36);
}

const GB = 1024 * 1024 * 1024;
/** "0.8 GB" / "640 MB": free room on a disk. */
const bytesText = n => (!(n >= 0) ? '' : n >= GB ? (n / GB).toFixed(1) + ' GB' : Math.round(n / 1048576) + ' MB');
/** What the red bar of an older server says ('' = nothing is wrong). storage = "storage" of /api/state ({ ok, message,
    messageZh, freeBytes, experimentsFolder }); zh = the page is in Chinese (the server words its message in both). */
function storageProblem(storage, zh) {
  if (!storage || typeof storage !== 'object' || storage.ok !== false) return '';
  const own = zh ? storage.messageZh || storage.message : storage.message || storage.messageZh;
  if (own) return String(own);
  return fin(storage.freeBytes) && storage.freeBytes < GB ? say('Only {0} free on the disk of the data folder: new data may not be saved.', bytesText(storage.freeBytes))
    : say('The data could not be saved: please check the disk of the data folder.');
}
/** Where a file of the Experiments folder (a path in it with /, or '' for the folder) is, for finding it by hand. */
function placeOf(folder, file) {
  const base = String(folder || 'PlateKineticsLive-data\\Experiments').replace(/[\\/]+$/, '');
  return file ? base + '\\' + String(file).replace(/\//g, '\\') : base;
}
/** Whether a full path lies in a folder (Windows: case does not count, / and \ are the same). */
function inFolder(path, folder) {
  const norm = p => String(p || '').replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
  const f = norm(folder), p = norm(path);
  return !!f && p.startsWith(f + '\\');
}

/* ---------- problems: banners at the top of the window, by severity ---------- */
const SEVERITY = { error: 0, warning: 1, info: 2 };
/** The problems of /api/state as banners: [{ key, code, severity, text, since, dismissible, instrument }], errors first.
    dismissed = { key: since } of the banners the user closed: a problem that comes again (another "since") shows
    again. A server without "problems" has the red bar of its "storage" only. */
function problemLines(state, dismissed, zh) {
  if (!isObj(state)) return [];
  if (!Array.isArray(state.problems)) {
    const text = storageProblem(state.storage, zh);
    return text ? [{ key: 'storage|', code: 'storage', severity: 'error', text, since: '', dismissible: false, instrument: '' }] : [];
  }
  const closed = isObj(dismissed) ? dismissed : {};
  return state.problems.filter(isObj).map((p, i) => {
    const severity = has(SEVERITY, p.severity) ? p.severity : 'info', key = String(p.code || '') + '|' + String(p.instrument || '');
    const own = zh ? p.messageZh || p.message : p.message || p.messageZh, text = own ? String(own) : String(p.code || '');
    return { key, code: String(p.code || ''), severity, text: p.instrument ? say('{0}: {1}', reader(p.instrument).name, text) : text,
      since: String(p.since || ''), dismissible: !!p.dismissible, instrument: String(p.instrument || ''), order: i };
  }).filter(p => !(p.dismissible && has(closed, p.key) && closed[p.key] === p.since))
    .sort((a, b) => SEVERITY[a.severity] - SEVERITY[b.severity] || a.order - b.order);
}

/* ---------- events: an experiment completed, stopped, was interrupted, brought no new data, could not be saved ---------- */
const KINDS = { completed: en('Completed'), stopped: en('Stopped early'), interrupted: en('Interrupted'), stuck: en('No new data'), 'save-failed': en('Could not be saved') };
const EVENT_KINDS = Object.keys(KINDS);
/** What tells an event apart from every other, also over a restart of the server. */
const eventKey = e => String(isObj(e) ? e.id : '') + '|' + String(isObj(e) ? e.at || '' : '');
/** The events of /api/state not announced yet (seen: the keys announced; an array or a Set), oldest first. */
function newEvents(events, seen) {
  const known = seen instanceof Set ? seen : new Set(Array.isArray(seen) ? seen : []);
  return (Array.isArray(events) ? events : []).filter(e => isObj(e) && e.id != null && !known.has(eventKey(e)))
    .sort((a, b) => (Number(a.id) || 0) - (Number(b.id) || 0));
}
/** The title of an event: "Reader 1: Completed". */
const eventTitle = e => (e.instrument ? say('{0}: {1}', reader(e.instrument).name, say(has(KINDS, e.kind) ? KINDS[e.kind] : String(e.kind || ''))) : say(has(KINDS, e.kind) ? KINDS[e.kind] : String(e.kind || '')));
/** What the server says about an event, in the language in use. */
const eventText = (e, zh) => String((zh ? e.messageZh || e.message : e.message || e.messageZh) || '');
/** Whether the settings ask to be told of this kind of event. */
function wanted(e, settings) {
  const kinds = isObj(settings) && isObj(settings.notify) && Array.isArray(settings.notify.kinds) ? settings.notify.kinds : EVENT_KINDS;
  return kinds.includes(e.kind) || !has(KINDS, e.kind);
}

/* ---------- the analysis of an experiment: saved into its .pk file, restored when it is opened ---------- */
/** The run a dataset belongs to: { runId, channel } ("Omega2-20260120-093000-ch2" -> channel 1). */
function runOfDataset(id) {
  const m = /^(.+)-ch(\d+)$/.exec(String(id || ''));
  return m && +m[2] >= 2 ? { runId: m[1], channel: +m[2] - 1 } : { runId: String(id || ''), channel: 0 };
}
/** A run id as the programs make them: "<instrument key>-yyyyMMdd-HHmmss". */
const isRunId = id => /^[A-Za-z0-9][A-Za-z0-9 ._()-]{0,100}-\d{8}-\d{6}$/.test(String(id || ''));
/** JSON with the keys of every object in order: the same data gives the same text. */
function stableJson(v) {
  if (Array.isArray(v)) return '[' + v.map(stableJson).join(',') + ']';
  if (isObj(v)) return '{' + Object.keys(v).sort().filter(k => v[k] !== undefined).map(k => JSON.stringify(k) + ':' + stableJson(v[k])).join(',') + '}';
  return JSON.stringify(v === undefined ? null : v);
}
/** What decides whether an analysis has changed: the names, the groups and their wells, the excluded wells, the notes,
    the settings (withSettings: false for an analysis that has none, made from a plate layout) and the operator - not
    the time it was made, its results (they follow from the rest) or the colours. */
function analysisSig(a, operator, withSettings) {
  if (!isObj(a)) return '';
  const sorted = list => (Array.isArray(list) ? list.filter(w => typeof w === 'string').slice().sort() : []);
  const page = isObj(a.page) ? a.page : {};
  return stableJson({ samples: isObj(a.samples) ? a.samples : {},
    groups: (Array.isArray(a.groups) ? a.groups : []).filter(isObj).map(g => ({ name: String(g.name || ''), wells: sorted(g.wells), role: g.role || '' })),
    notes: String(a.notes || '').trim(), excluded: sorted(page.excluded),
    settings: withSettings === false ? null : isObj(a.settings) ? a.settings : {},
    operator: String(operator == null ? a.operator || '' : operator).trim() });
}
/** Whether a saved analysis holds the settings it was made with (one made from a plate layout holds none). */
const hasSettings = a => isObj(a) && isObj(a.settings) && Object.keys(a.settings).length > 0;
/** The problems of one instrument (for its tile) in the language in use; "unreadable" is the tile's own message. */
function instrumentProblems(state, key, zh) {
  return (isObj(state) && Array.isArray(state.problems) ? state.problems : []).filter(p => isObj(p) && p.instrument === key && p.code !== 'unreadable')
    .map(p => String((zh ? p.messageZh || p.message : p.message || p.messageZh) || p.code || ''));
}
/** The analysis to save: the page's, with who saves it and who ran the experiment, and the channel it came from. */
function analysisToSave(a, extra) {
  const x = extra || {}, out = Object.assign({}, a);
  out.savedAt = localStamp(fin(x.now) ? x.now : Date.now());          // (the program writes its own when it keeps it)
  if (x.savedBy) out.savedBy = String(x.savedBy);
  out.operator = x.operator ? String(x.operator) : null;
  if (fin(x.channel) && x.channel > 1) out.channel = x.channel;
  return out;
}
/** Sample names (and groups) as a plate layout gives them, in any of the shapes the programs write: { A1: 'name' },
    { A1: { name, group } }, { wells: { ... } } or [{ well, name, group }]. -> { names: { A1 }, groups: { A1 } }. */
function layoutNames(layout) {
  const names = {}, groups = {};
  const put = (well, v) => {
    if (typeof well !== 'string' || !/^[A-P]([1-9]|1\d|2[0-4])$/.test(well)) return;
    if (typeof v === 'string' || typeof v === 'number') { if (String(v).trim()) names[well] = String(v).trim(); return; }
    if (!isObj(v)) return;
    const name = v.name != null ? v.name : v.sample != null ? v.sample : v.id;
    if (name != null && String(name).trim()) names[well] = String(name).trim();
    if (v.group != null && String(v.group).trim()) groups[well] = String(v.group).trim();
  };
  const src = isObj(layout) && isObj(layout.wells) ? layout.wells : layout;
  if (Array.isArray(src)) for (const v of src) { if (isObj(v)) put(v.well || v.id, v); }
  else if (isObj(src)) for (const well in src) put(well, src[well]);
  return { names, groups };
}
/** An analysis that names the wells of a fresh dataset after a plate layout (and groups them by its groups): the
    other wells keep the names they have. */
function namesAnalysis(ds, layout) {
  const { names, groups } = layoutNames(layout);
  if (!Object.keys(names).length && !Object.keys(groups).length) return null;
  const samples = {};
  for (const id of ds.order || []) { const w = ds.wells[id]; const n = has(names, id) ? names[id] : w && w.name; if (n) samples[id] = n; }
  const byName = new Map();
  for (const id of ds.order || []) if (has(groups, id)) { if (!byName.has(groups[id])) byName.set(groups[id], []); byName.get(groups[id]).push(id); }
  return { format: 'plate-kinetics-live-analysis/1', samples, groups: [...byName].map(([name, wells]) => ({ name, wells })), notes: ds.notes || '' };
}
/** What became of the Excel file when an analysis was saved (or the file made again): a sentence, '' when written. */
function xlsxText(xlsx) {
  const s = String(xlsx || '');
  if (s.startsWith('saved-as:')) return say('The Excel file was open or had been changed, so a new one was made: {0}', s.slice(9).split('/').pop());
  if (s.startsWith('failed:')) return say('The Excel file could not be made: {0}', s.slice(7));
  return '';
}

/* ---------- the annotation of an experiment, the same on every device that shows it ----------
   An experiment has one annotation: analysis.json in its .pk on the instrument PC (GET/POST /api/analysis; online the
   same routes of the online server, which forwards the save of an annotation and nothing else). For each dataset of an
   experiment a device keeps the version of the instrument the dataset shows ("rev") and what the dataset said then (its
   signature, analysisSig): what differs from that is a change made on this device.
   - A change is saved by itself about 3 s after the last one (Save or Ctrl+S: at once), where this device may save: in
     the program's window, and online when signed in. Online and not signed in a change stays in this browser.
   - A save that does not get through (the instrument PC cannot be reached) is tried again every 30 s.
   - A newer version of the instrument goes into a dataset without changes by itself. With changes it waits for the
     user ("Newer on the instrument"): Restore from instrument takes it, Save puts this device's over it (the last save wins).
   - The workbook (.xlsx) is made again on Save / Ctrl+S ("now"), when the experiment is left or closed ("leave"), and by
     the program itself at most 10 minutes after an automatic save, which does not make it (program's window only). The
     program says what became of it with every save ("xlsx": written, saved-as:, current, pending = it makes it later
     itself, failed:).
   Everything is driven by tick(), every second and after the user did something, with the time of env.now(): the tests
   run it on a clock of their own. The program's revs are compared as "r<n>"; a program without revs is told apart by
   the time of its save ("t<savedAt>"); '' = nothing saved; null = not known yet. */
const SYNC = { saveAfterMs: 3000, retryMs: 30000, checkMs: 10000, askMs: 5000 };
/** A short fingerprint of a text (53 bits, base 36): what a browser keeps of a dataset's signature to know it again. */
function textHash(text) {
  const s = String(text == null ? '' : text);
  let h1 = 0xdeadbeef, h2 = 0x41c6ce57;
  for (let i = 0; i < s.length; i++) { const c = s.charCodeAt(i); h1 = Math.imul(h1 ^ c, 2654435761); h2 = Math.imul(h2 ^ c, 1597334677); }
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  return (4294967296 * (2097151 & h2) + (h1 >>> 0)).toString(36);
}
/** A rev of the program ("analysisRev" of /api/state and /api/runs, "rev" of /api/analysis) as compared here. */
const revKey = v => (v == null || v === '' || String(v) === '0' ? '' : 'r' + String(v));
/** The version an answer of GET /api/analysis describes. */
function versionOf(answer) {
  if (!isObj(answer)) return '';
  const r = revKey(answer.rev), a = isObj(answer.analysis) ? answer.analysis : null;
  return r || (a ? 't' + String(answer.savedAt || a.savedAt || '') : '');
}
const revNumber = key => (/^r\d+$/.test(String(key || '')) ? +String(key).slice(1) : NaN);
/** Whether two annotations say the same (names, groups and their wells, exclusions, notes, settings). */
const sameAnnotation = (a, b) => (!isObj(a) && !isObj(b)) || (isObj(a) && isObj(b) && analysisSig(a, '', true) === analysisSig(b, '', true));

/**
 * env = { now() -> ms,
 *   look(dsId) -> { sig, analysis } | null: the page's annotation of a dataset (null: not on the page),
 *   apply(dsId, analysis, undo): puts an annotation into a dataset (throws for one it cannot take),
 *   datasets(runId) -> [dsId]: the datasets of the experiment on the page,
 *   online() -> bool, canSave(x) -> bool: this device may save into the experiment now,
 *   fileOnly(x) -> bool: a .pk outside the Experiments folder, saved by the program's window (no revs),
 *   canBook(x) -> bool: its workbook can be made (the experiment's file is known),
 *   extra(x, dsId) -> { savedBy, operator, channel }: what goes with a save (analysisToSave),
 *   save(x, analysis, { xlsx: 'now' | 'leave' | '' (the workbook follows), keepalive }) -> Promise<{ rev, savedAt,
 *   savedBy, xlsx, previous }> (rejects with .status; 409: no second try would change it), savedText(x) -> what Save says
 *   when it is done ('' = "Saved to the experiment"),
 *   load(x) -> Promise<{ analysis, rev, savedAt, savedBy, file, previous }>, revert(x) -> Promise<answer>,
 *   workbook(x, { how: 'now' | 'leave', keepalive }) -> Promise<{ xlsx }>, file(x, file): the experiment's file,
 *   store: { get(dsId), set(dsId, { r, h } | null) }: what the browser keeps over a reload,
 *   busy() -> bool: not now (a half-typed name, a drag), toast(text, ms), changed(): draw again,
 *   signIn(): online, ask the user to sign in; signedOut(): a save was refused for want of a sign-in }
 */
function createSync(env) {
  const records = new Map();
  let last = null;                                  // what was in view at the last tick: { x, dsId, key }
  const now = () => env.now();
  const fileOnly = x => !!(env.fileOnly && env.fileOnly(x));
  const ready = x => x.loaded || fileOnly(x);
  const mayWrite = x => !!env.canSave(x) && !x.unreachable && !x.blocked && !x.denied;
  const canBook = x => !env.online() && !fileOnly(x) && !!(env.canBook && env.canBook(x));
  const message = e => String((e && e.message) || e || '');
  /** What Save says when it is done (a .pk outside the Experiments folder names its file). */
  const savedWord = x => (env.savedText && env.savedText(x)) || say('Saved to the experiment');

  /** The record of an experiment (extra: the shell's own fields, given when it is made). */
  function exp(runId, extra) {
    if (!records.has(runId)) records.set(runId, Object.assign({ runId, rev: null, saved: null, savedAt: '', savedBy: '', loaded: false, loading: null,
      epoch: 0, remote: '', checkedAt: -Infinity, bases: {}, seen: {}, starts: {}, pending: {}, inflight: null, offline: false, denied: false, error: '',
      refused: null, retryAt: 0, retryDs: '', bookDue: 0, bookMode: '', prev: undefined, unreachable: false, blocked: false, operator: null, news: '' }, extra || {}));
    return records.get(runId);
  }
  /** What a dataset shows now (kept: the last look of a dataset that is no longer on the page); `at`: when it last
      changed, the operator typed in the strip included. */
  function observe(x, dsId) {
    const got = env.look(dsId), s = x.seen[dsId];
    if (!got) return s || null;
    if (!s || s.sig !== got.sig || s.op !== x.operator) x.seen[dsId] = { sig: got.sig, at: now(), analysis: got.analysis, op: x.operator };
    else s.analysis = got.analysis;                // the newest results of a run that is measured
    return x.seen[dsId];
  }
  /** The state or the list of runs says the instrument has a version this device has not seen yet. */
  const remoteNewer = x => { const a = revNumber(x.remote), b = revNumber(x.rev); return !!x.remote && x.remote !== x.rev && !(fin(a) && fin(b) && a < b); };
  function based(x, dsId, rev, sig) {
    x.bases[dsId] = { rev, sig };
    if (env.store) env.store.set(dsId, sig == null ? { r: rev } : { r: rev, h: textHash(sig) });
  }
  /** Changes made on this device: the dataset differs from the version it is based on, or an operator was typed. */
  const changedHere = (x, dsId) => { const b = x.bases[dsId], s = x.seen[dsId]; return !!(b && s) && (s.sig !== b.sig || x.operator != null); };
  /** The instrument has a version this dataset is not based on. */
  const behind = (x, dsId) => { const b = x.bases[dsId]; return !!b && !!x.rev && b.rev !== x.rev; };

  /** An answer of the program about the annotation it has. Older than what is known: left aside. */
  function take(x, answer, epoch, o) {
    if (!isObj(answer)) return false;
    if (env.file && answer.file !== undefined) env.file(x, answer.file);
    if (fileOnly(x)) { x.loaded = true; return false; }              // the file's own annotation counts, not the program's copy
    if (epoch !== x.epoch) return false;                            // asked before this device saved, restored or undid
    known(x, answer);
    const ver = versionOf(answer), was = x.rev, a = revNumber(ver), b = revNumber(was);
    if (!(o && o.force) && fin(a) && fin(b) && a < b) return false;
    x.loaded = true; x.unreachable = false;
    if (x.offline && !x.inflight) { x.offline = false; if (x.retryAt) x.retryAt = now(); }     // it answers again: try the save now
    const saved = isObj(answer.analysis) ? answer.analysis : null;
    x.savedAt = String(answer.savedAt || (saved && saved.savedAt) || '');
    x.savedBy = String(answer.savedBy || (saved && saved.savedBy) || '');
    if (ver === was) return false;
    const same = sameAnnotation(x.saved, saved);
    x.saved = saved;
    x.rev = ver;
    if (same) {                                     // the same annotation under another version: nothing to put in
      for (const dsId of Object.keys(x.bases)) if (x.bases[dsId].rev === was) based(x, dsId, ver, x.bases[dsId].sig);
    } else if (o && o.theirs && was != null) x.news = ver;
    return true;
  }
  /** What an answer says beyond the annotation: the version before the last save ("previous": what Undo last save puts
      back; null: none), and whether the program says what becomes of the workbook (it then decides when it follows). */
  function known(x, answer) {
    if ('previous' in answer) { x.prev = isObj(answer.previous) ? answer.previous : null; x.bookMode = 'program'; }
    if (answer.xlsx) x.bookMode = 'program';
  }
  /** What became of the workbook with a save or an undo, as the program says it ("pending": the program makes it later
      itself; this page asks for it when the experiment is left). tell(text): what to say. */
  function booked(x, a, tell) {
    const book = String(a.xlsx || '');
    x.bookDue = book === 'pending' ? x.bookDue || now() : 0;
    tell(book ? xlsxText(book) : '');
  }
  function load(x) {
    if (x.loading) return x.loading;
    const epoch = x.epoch;
    x.checkedAt = now();
    x.loading = Promise.resolve().then(() => env.load(x)).then(answer => { take(x, answer, epoch, { theirs: true }); }, e => {
      const status = e && e.status;
      if (status === 404 || status === 405) { x.unreachable = true; x.loaded = true; }    // no experiment of this program, or an older program
      else x.offline = true;
    }).then(() => { x.loading = null; env.changed(); return x; });
    return x.loading;
  }
  /** Puts an annotation into a dataset, which is then based on the version of the instrument. */
  function put(x, dsId, a, undo) {
    try { env.apply(dsId, a, undo); } catch (e) {
      x.blocked = /newer version/i.test(message(e));
      x.error = message(e);
      env.toast(say('The annotation could not be loaded: {0}', x.error), 8000);
      return false;
    }
    const got = env.look(dsId);
    if (got) { x.seen[dsId] = { sig: got.sig, at: now(), analysis: got.analysis, op: x.operator }; based(x, dsId, x.rev, got.sig); }
    return true;
  }
  /** A newer version of the instrument goes into every dataset of the experiment without changes of this device. */
  function follow(x, inView) {
    if (!ready(x) || fileOnly(x) || x.rev == null) return;
    for (const dsId of env.datasets(x.runId)) {
      const b = x.bases[dsId];
      if (!b || b.rev === x.rev || x.pending[dsId]) continue;
      const s = observe(x, dsId);
      if (!s || changedHere(x, dsId)) continue;              // changes of this device: the user decides
      if (!x.saved || sameAnnotation(x.saved, s.analysis)) { based(x, dsId, x.rev, s.sig); continue; }
      if (env.busy && env.busy()) continue;                  // not under a half-typed name or a drag: at the next tick
      if (put(x, dsId, x.saved, false) && dsId === inView && x.news === x.rev) {
        x.news = '';
        if (x.savedBy) env.toast(say('{0} saved a newer annotation: it is shown now', x.savedBy), 6000);
      }
    }
  }
  /** Saves what a dataset shows. o = { workbook: 'now' | 'leave' (the .xlsx follows), explicit (Save, Ctrl+S: say so),
      keepalive }. */
  function save(x, dsId, o) {
    const opt = o || {};
    const got = env.look(dsId) || (x.seen[dsId] ? { sig: x.seen[dsId].sig, analysis: x.seen[dsId].analysis } : null);
    if (!got || !isObj(got.analysis)) return Promise.resolve(false);
    const posted = analysisToSave(got.analysis, env.extra ? env.extra(x, dsId) : {});
    const sig = got.sig, typed = x.operator, how = opt.workbook && canBook(x) ? opt.workbook : '';
    x.epoch++;
    const run = Promise.resolve().then(() => env.save(x, posted, { xlsx: how, keepalive: !!opt.keepalive })).then(answer => {
      const a = isObj(answer) ? answer : {};
      Object.assign(x, { inflight: null, offline: false, denied: false, error: '', refused: null, loaded: true, news: '' });
      if (x.retryDs === dsId) { x.retryAt = 0; x.retryDs = ''; }
      if (a.savedAt) posted.savedAt = String(a.savedAt);
      if (a.savedBy) posted.savedBy = String(a.savedBy);
      x.saved = posted; x.savedAt = String(posted.savedAt || ''); x.savedBy = String(posted.savedBy || '');
      x.rev = revKey(a.rev) || 't' + x.savedAt;
      x.epoch++;                                    // what was asked before this answer is older
      based(x, dsId, x.rev, sig);
      if (x.operator === typed) x.operator = null;
      known(x, a);
      booked(x, a, excel => {
        if (opt.explicit) env.toast([savedWord(x), excel].filter(Boolean).join(' '), excel ? 9000 : 4000);
        else if (excel) env.toast(excel, 9000);
      });
      return true;
    }, e => {
      x.inflight = null;
      const status = e && e.status;
      if (status === 403 && env.online()) { x.denied = true; if (env.signedOut) env.signedOut(); }
      else if (!status || status === 403 || status === 408 || status === 429 || status >= 500) {
        x.offline = !status || status === 408 || status === 502 || status === 503 || status === 504;
        x.error = x.offline ? '' : message(e);
        x.retryAt = now() + SYNC.retryMs; x.retryDs = dsId;
      } else { x.error = message(e); x.refused = sig; }
      if (opt.explicit) env.toast(x.denied ? say('Sign in to save them on the instrument PC') : x.offline ? offlineText(env.online()) : say('Not saved: {0}', x.error), 8000);
      return false;
    }).then(done => { env.changed(); return done; });
    x.inflight = { dsId, sig, run };
    env.changed();
    return run;
  }
  /** The workbook made again (POST /api/analysis/xlsx). What is said: only a workbook that had to be
      named otherwise or could not be made; Save / Ctrl+S (explicit) also that it is saved; quiet: nothing (the text is
      handed back, failures too). */
  function workbook(x, o) {
    const opt = o || {};
    x.bookDue = 0;
    if (!canBook(x)) { if (opt.explicit) env.toast(savedWord(x)); return Promise.resolve(''); }
    return Promise.resolve().then(() => env.workbook(x, { how: opt.how || 'leave', keepalive: !!opt.keepalive })).then(answer => {
      const book = isObj(answer) ? String(answer.xlsx || '') : '';
      if (book === 'pending' && !x.bookDue) x.bookDue = now();      // open in a program, or the run is still measured: later
      return xlsxText(book);
    }, e => (opt.explicit || opt.quiet ? say('The Excel file could not be made: {0}', message(e)) : '')).then(text => {
      if (!opt.quiet) { if (text) env.toast(text, 9000); else if (opt.explicit) env.toast(savedWord(x)); }
      return text;
    });
  }
  /** The experiment (this dataset of it) is no longer in view: its changes are saved now, and its workbook made. */
  function leave(x, dsId, o) {
    const opt = o || {};
    if (!x || !ready(x) || x.pending[dsId]) return;
    const s = observe(x, dsId);
    if (s && changedHere(x, dsId) && !behind(x, dsId) && mayWrite(x) && x.refused !== s.sig) {
      if (x.inflight) { x.retryDs = dsId; x.retryAt = now(); return; }      // once the save under way has its answer
      save(x, dsId, { workbook: opt.workbook ? 'leave' : '', keepalive: opt.keepalive });
      return;
    }
    if (opt.workbook && x.bookDue && !x.inflight) workbook(x, { how: 'leave', keepalive: opt.keepalive });
  }
  /** A dataset found on the page without a version (the page has put its session back after a reload): what the
      browser kept tells whether it shows that version or has changes of this device; without it, a dataset that
      shows what the instrument has is based on that, else the user decides. */
  function adopt(x, dsId) {
    const got = env.look(dsId);
    if (!got) return;
    if (!x.seen[dsId] || x.seen[dsId].sig !== got.sig) x.seen[dsId] = { sig: got.sig, at: now(), analysis: got.analysis, op: x.operator };
    const kept = env.store ? env.store.get(dsId) : null, rev = isObj(kept) && kept.r != null ? String(kept.r) : null;
    if (isObj(kept) && kept.h && kept.h === textHash(got.sig)) x.bases[dsId] = { rev, sig: got.sig };
    else if (isObj(kept)) x.bases[dsId] = { rev, sig: null };
    else if (!x.saved || sameAnnotation(x.saved, got.analysis)) based(x, dsId, x.rev, got.sig);
    else based(x, dsId, null, null);
  }

  /** Every second, and after the user did something. view = { x, dsId, visible } of the experiment in view, or null. */
  function tick(view) {
    const t = now(), key = view && view.x ? view.x.runId + '|' + view.dsId : '';
    if (last && last.key !== key) leave(last.x, last.dsId, { workbook: !key || view.x !== last.x });
    last = key ? { x: view.x, dsId: view.dsId, key } : null;
    if (key) {
      const x = view.x, dsId = view.dsId, visible = view.visible !== false;
      if (!fileOnly(x) && !x.loading && !x.inflight && !x.unreachable && (!x.loaded || visible) && t - x.checkedAt >= SYNC.checkMs) load(x);
      if (ready(x) && visible && !x.pending[dsId]) {
        if (!x.bases[dsId]) adopt(x, dsId);
        const s = observe(x, dsId);
        follow(x, dsId);
        // (a little early rather than a second late: the ticks of a browser come a few ms apart from a second)
        if (s && changedHere(x, dsId) && !behind(x, dsId) && mayWrite(x) && !x.inflight && t - s.at >= SYNC.saveAfterMs - 250
          && (!x.retryAt || t >= x.retryAt) && x.refused !== s.sig) {
          // the instrument has a version this device has not seen: that first (with changes here, the user decides)
          if (remoteNewer(x) && !fileOnly(x)) { if (!x.loading) load(x); } else save(x, dsId, {});
        }
      }
    }
    for (const x of records.values()) {
      // a save that did not get through, or a dataset that was left while another save was under way
      if (x.retryDs && x.retryAt && t >= x.retryAt && !x.inflight && ready(x) && !x.pending[x.retryDs]) {
        const dsId = x.retryDs, s = observe(x, dsId);
        if (s && changedHere(x, dsId) && !behind(x, dsId) && mayWrite(x) && x.refused !== s.sig && !(remoteNewer(x) && !fileOnly(x)))
          save(x, dsId, { workbook: !last || last.x !== x ? 'leave' : '' });
        else if (!s || !changedHere(x, dsId) || behind(x, dsId)) { x.retryAt = 0; x.retryDs = ''; }
      }
      // the state or the list of runs says the instrument has another version: ask for it (at most every 5 s)
      if (remoteNewer(x) && x.loaded && !fileOnly(x) && !x.loading && !x.inflight && t - x.checkedAt >= SYNC.askMs) load(x);
    }
  }
  /** Save or Ctrl+S: what is changed is saved at once (also over a newer version: the last save wins); with nothing
      changed the workbook is made again. Online and not signed in: the user is asked to sign in. */
  function saveNow(x, dsId) {
    if (!env.canSave(x)) { if (env.online() && env.signIn) env.signIn(); else env.toast(say('This version of the analysis page cannot save into the experiment'), 6000); return Promise.resolve(false); }
    if (x.unreachable || x.blocked) { env.toast(say('Not saved: {0}', x.blocked ? x.error : say('this experiment was not recorded by this program')), 8000); return Promise.resolve(false); }
    if (x.inflight) return x.inflight.run.then(() => saveNow(x, dsId));
    const s = observe(x, dsId);
    x.denied = false;
    if (!s) return Promise.resolve(false);
    if (changedHere(x, dsId) || !x.bases[dsId]) return save(x, dsId, { workbook: 'now', explicit: true });
    return workbook(x, { explicit: true, how: 'now' });
  }
  /** Restore from instrument: the changes of this device go, the instrument's version comes (its newest; when the
      instrument PC cannot be reached, the newest known). Nothing saved: what the dataset showed when it was opened. */
  function restore(x) {
    if (x.inflight) return Promise.resolve(false);
    const epoch = ++x.epoch;
    const back = () => {
      let done = 0;
      for (const dsId of env.datasets(x.runId)) { const a = x.saved || x.starts[dsId]; if (a && !x.pending[dsId] && put(x, dsId, a, true)) done++; }
      Object.assign(x, { operator: null, error: '', refused: null, denied: false, retryAt: 0, retryDs: '', news: '' });
      if (done) env.toast(say('The annotation saved in the experiment is shown again'));
      return done > 0;
    };
    const asked = fileOnly(x) ? Promise.resolve(back()) : Promise.resolve().then(() => env.load(x)).then(answer => { take(x, answer, epoch, { force: true }); return back(); },
      e => { if (x.loaded) return back(); env.toast(say('The annotation could not be loaded: {0}', message(e)), 8000); return false; });
    return asked.then(r => { env.changed(); return r; });
  }
  /** Undo last save (the program's window only): the program puts the version before its last save back, and every
      dataset of the experiment shows it (a step the page can undo). */
  function undoSave(x) {
    if (x.inflight || env.online() || fileOnly(x) || !env.revert) return Promise.resolve(false);
    const epoch = ++x.epoch;
    return Promise.resolve().then(() => env.revert(x)).then(answer => (isObj(answer) && 'analysis' in answer ? answer : env.load(x))).then(answer => {
      take(x, answer, epoch, { force: true });
      for (const dsId of env.datasets(x.runId)) { const a = x.saved || x.starts[dsId]; if (a && !x.pending[dsId]) put(x, dsId, a, true); }
      Object.assign(x, { operator: null, error: '', refused: null, news: '' });
      booked(x, isObj(answer) ? answer : {}, excel => { if (excel) env.toast(excel, 9000); });     // (else when the experiment is left)
      env.toast(say('The annotation is back to the version before the last save'));
      return true;
    }, e => { env.toast(say('The last save could not be undone: {0}', message(e)), 8000); return false; }).then(r => { env.changed(); return r; });
  }
  /** Where the annotation of a dataset stands, for the strip: state none | same | saving | local | newer, and why. */
  function status(x, dsId) {
    const st = { state: 'none', reason: '', error: x.error, savedAt: x.savedAt, savedBy: x.savedBy, hasSaved: !!x.saved || !!x.savedAt, canRestore: false, canUndo: false };
    const here = changedHere(x, dsId);
    if (x.inflight && x.inflight.dsId === dsId) st.state = 'saving';
    else if (behind(x, dsId)) st.state = 'newer';            // with changes: the user decides; without: it goes in at the next tick
    else if (here) {
      st.reason = x.denied || !env.canSave(x) ? (env.online() ? 'signin' : 'cannot') : x.unreachable ? 'unreachable' : x.blocked ? 'error'
        : x.offline ? 'offline' : x.error ? 'error' : '';
      st.state = st.reason ? 'local' : 'saving';
    } else if (st.hasSaved) st.state = 'same';
    st.canRestore = (st.state === 'local' || st.state === 'newer') && (!!x.saved || !!x.starts[dsId]);
    // (only when the program has a version from before the last save)
    st.canUndo = st.state === 'same' && !env.online() && !fileOnly(x) && !x.unreachable && /^r/.test(String(x.rev || '')) && isObj(x.prev);
    return st;
  }
  return { exp, known: runId => records.get(runId) || null, records: () => [...records.values()], load, tick, saveNow, restore, undoSave, status,
    /** A dataset of the experiment has just been made: nothing is done to it until opened() (the instrument's version is
        being put into it). */
    opening(x, dsId) { x.pending[dsId] = true; },
    /** A dataset was just put on the page with the instrument's version (or the names of a plate layout) in it.
        edited: the user had begun to work on it before that came, so it was left as it was. */
    opened(x, dsId, edited) {
      delete x.pending[dsId];
      const got = env.look(dsId);
      if (!got) return;
      x.seen[dsId] = { sig: got.sig, at: now(), analysis: got.analysis, op: x.operator };
      if (!edited) x.starts[dsId] = got.analysis;
      based(x, dsId, x.rev, edited ? null : got.sig);
    },
    /** analysisRev of /api/state or /api/runs for a run. */
    remote(runId, rev) { const x = records.get(runId); if (x) x.remote = revKey(rev); },
    /** The page is being closed: what is changed goes now (a request that outlives the page). */
    flush() { if (last) leave(last.x, last.dsId, { workbook: true, keepalive: true }); },
    /** Signed in (online): saves refused for want of a sign-in may go again. */
    signedIn() { for (const x of records.values()) x.denied = false; },
    changedHere, behind, fileOnly };
}
/** The reason a save did not get through when the instrument PC (online) or the background program does not answer. */
const offlineText = online => (online ? say('The instrument PC cannot be reached: trying again every 30 s') : say('The background program does not answer: trying again every 30 s'));
/** What the strip says about the annotation: { chip, note } for status() of createSync(); o = { online, now }. In the
    program's window the green state is the time of the save ("Saved 14:32 · user (PC)"). */
function syncTexts(st, o) {
  const at = stampMs(st.savedAt), who = [fin(at) ? clockText(at, o.now) : '', st.savedBy || ''].filter(Boolean).join(' · ');
  const saved = who ? say('Saved {0}', who) : st.hasSaved ? say('Saved') : '';
  if (st.state === 'same') return o.online ? { chip: say('Same as the instrument'), note: saved } : { chip: saved, note: '' };
  if (st.state === 'saving') return { chip: say('Saving…'), note: '' };
  if (st.state === 'newer') return { chip: say('Newer on the instrument'), note: saved };
  if (st.state === 'local') {
    const why = st.reason === 'signin' ? say('Sign in to save them on the instrument PC') : st.reason === 'offline' ? offlineText(o.online)
      : st.reason === 'cannot' ? say('This version of the analysis page cannot save into the experiment')
        : st.reason === 'unreachable' ? say('Not saved: {0}', say('this experiment was not recorded by this program')) : st.error ? say('Not saved: {0}', st.error) : '';
    return { chip: say('Changes only on this device'), note: why };
  }
  return { chip: '', note: '' };
}

/* ---------- the data list of the side bar: experiments that ended long ago are folded away ---------- */
const FOLD_MS = 7 * 86400e3;
/** The start of a run by its id ("...-20260120-093000"), NaN for another id. */
function runStartMs(id) {
  const m = /-(\d{4})(\d\d)(\d\d)-(\d\d)(\d\d)(\d\d)$/.exec(runOfDataset(id).runId);
  return m ? new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]).getTime() : NaN;
}
/** When the run of a dataset had its last reading (ms), NaN when the dataset is no run of an instrument. */
function datasetEndMs(ds) {
  const start = runStartMs(ds && ds.id);
  if (!fin(start)) return NaN;
  let last = 0;
  for (let i = (ds.t ? ds.t.length : 0) - 1; i >= 0; i--) if (fin(ds.t[i])) { last = ds.t[i]; break; }
  return start + last * 3600e3;
}
/** Whether a dataset of the side bar is folded away: a run that ended more than 7 days ago and was not opened in the
    last 7 days, which is neither measured nor the one in view. o = { live: Set of run ids, seen: { id: ms }, current, now }. */
function foldable(ds, o) {
  if (!ds || ds.id === o.current) return false;
  const run = runOfDataset(ds.id).runId, end = datasetEndMs(ds);
  if (!fin(end) || (o.live && o.live.has(run))) return false;
  const seen = o.seen && fin(o.seen[ds.id]) ? o.seen[ds.id] : 0;
  return o.now - Math.max(end, seen) > FOLD_MS;
}

/* ---------- plate layouts: names and groups prepared before a run ---------- */
const ROW_LETTERS = 'ABCDEFGHIJKLMNOP';
const wellId = (r, c) => ROW_LETTERS[r] + (c + 1);
function wellPos(id) {
  const m = /^([A-P])([1-9]|1\d|2[0-4])$/.exec(String(id || ''));
  return m ? { r: ROW_LETTERS.indexOf(m[1]), c: +m[2] - 1 } : null;
}
/** The wells of a plate in plate order (A1, A2, ..., B1, ...). */
function plateWells(rows, cols) {
  const out = [];
  for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) out.push(wellId(r, c));
  return out;
}
/** The wells of the rectangle between two wells. */
function wellRange(a, b) {
  const p = wellPos(a), q = wellPos(b);
  if (!p || !q) return [];
  const out = [];
  for (let r = Math.min(p.r, q.r); r <= Math.max(p.r, q.r); r++) for (let c = Math.min(p.c, q.c); c <= Math.max(p.c, q.c); c++) out.push(wellId(r, c));
  return out;
}
/** A new, empty layout. */
const newLayout = (rows, cols) => ({ id: '', name: '', plate: { rows, cols }, wells: {}, updatedAt: null });
/** The layout with the wells `ids` given a name and/or a group (patch: { name, group }; '' clears it). */
function editLayout(layout, ids, patch) {
  const wells = Object.assign({}, layout.wells), p = patch || {};
  for (const id of ids) {
    const cell = Object.assign({ name: '', group: '' }, wells[id]);
    if (p.name != null) cell.name = String(p.name).trim();
    if (p.group != null) cell.group = String(p.group).trim();
    if (cell.name || cell.group) wells[id] = cell; else delete wells[id];
  }
  return Object.assign({}, layout, { wells });
}
/** The groups of a layout, in the order of the plate: [{ name, wells }]. */
function layoutGroups(layout) {
  const out = new Map(), plate = layout && layout.plate ? layout.plate : { rows: 8, cols: 12 };
  for (const id of plateWells(plate.rows, plate.cols)) {
    const cell = layout && layout.wells && layout.wells[id];
    if (cell && cell.group) out.set(cell.group, (out.get(cell.group) || 0) + 1);
  }
  return [...out].map(([name, wells]) => ({ name, wells }));
}
/** "96 wells · 24 named · 4 groups". */
function layoutMeta(layout) {
  const plate = layout && layout.plate ? layout.plate : { rows: 8, cols: 12 }, wells = isObj(layout && layout.wells) ? layout.wells : {};
  const named = Object.keys(wells).filter(id => wells[id] && wells[id].name).length, groups = layoutGroups(layout).length;
  return [say('{0} wells', plate.rows * plate.cols), say('{0} named', named), say('{0:group}', groups)].join(' · ');
}

/* ---------- settings (GET/POST /api/settings) ---------- */
/** The settings with everything in place: { instruments, notify: { sound, kinds }, keepAwake, nextExperiment, operatorDefault }. */
function settingsOf(raw) {
  const s = isObj(raw) ? raw : {}, notify = isObj(s.notify) ? s.notify : {};
  const instruments = {}, next = {};
  if (isObj(s.instruments)) for (const k in s.instruments) if (isObj(s.instruments[k]) && typeof s.instruments[k].displayName === 'string' && s.instruments[k].displayName.trim())
    instruments[k] = { displayName: s.instruments[k].displayName.trim() };
  if (isObj(s.nextExperiment)) for (const k in s.nextExperiment) {
    const n = s.nextExperiment[k];
    if (isObj(n) && ((n.name && String(n.name).trim()) || n.layoutId)) next[k] = { name: n.name ? String(n.name).trim() : '', layoutId: n.layoutId ? String(n.layoutId) : '' };
  }
  return { instruments, notify: { sound: notify.sound !== false, kinds: Array.isArray(notify.kinds) ? EVENT_KINDS.filter(k => notify.kinds.includes(k)) : EVENT_KINDS.slice() },
    keepAwake: s.keepAwake !== false, nextExperiment: next, operatorDefault: typeof s.operatorDefault === 'string' ? s.operatorDefault.trim() : '' };
}
/** The next experiment of an instrument in words: "Batch 12 · layout RT-QuIC 24 x 4" ('' for none). */
function nextText(next, layouts) {
  if (!isObj(next) || (!next.name && !next.layoutId)) return '';
  const layout = next.layoutId ? (Array.isArray(layouts) ? layouts : []).find(l => isObj(l) && l.id === next.layoutId) : null;
  return [next.name || '', next.layoutId ? say('layout {0}', layout ? layout.name || next.layoutId : next.layoutId) : ''].filter(Boolean).join(' · ');
}

/* ---------- the protocol details of a run ---------- */
/* The program reads how a run was set up from the instrument software's own files (BMG: Omega's protocol row; Archimed:
   the experiment file) and hands it over ready to show, in both languages, as "protocolDetails":
     { sections: [{ title, titleZh, rows: [{ label, labelZh, value, valueZh }] }],
       steps: [{ stage, kind, repeats, label, labelZh, steps: [{ temperature, hold, ramp, collect }] }] (PCR only) }   or null.
   The page knows nothing of the instruments' own field names or codes: it only shows these. The name of a stage
   (label / labelZh, e.g. "Stage 1 · Cycling × 99") is the program's (its table of stage kinds is the only one); a stage
   without one is named by its number and its kind as the program gives it. Values come from the instruments' files:
   they are shown as text, never as markup. */
/** A text of the program's data: a string or a finite number, else ''. */
const plainText = v => (typeof v === 'string' ? v.trim() : typeof v === 'number' && fin(v) ? String(v) : '');
/** protocolDetails in the language in use: { sections: [{ title, rows: [{ label, value }] }], stages: [line], ramp }
    (stages: the temperature program, one line per stage; ramp: the one ramp rate of every step, '' when they differ),
    or null when nothing is known. Whatever is missing or of an odd type is left out. */
function protocolView(pd, zh) {
  if (!isObj(pd)) return null;
  const pick = (o, name) => (zh && plainText(o[name + 'Zh'])) || plainText(o[name]) || plainText(o[name + 'Zh']);
  const sections = (Array.isArray(pd.sections) ? pd.sections : []).filter(isObj).map(s => ({
    own: plainText(s.title), title: pick(s, 'title'),
    rows: (Array.isArray(s.rows) ? s.rows : []).filter(isObj).map(r => ({ label: pick(r, 'label'), value: pick(r, 'value') })).filter(r => r.label && r.value)
  })).filter(s => s.rows.length);
  const stagesIn = (Array.isArray(pd.steps) ? pd.steps : []).filter(isObj)
    .map(s => ({ s, steps: (Array.isArray(s.steps) ? s.steps : []).filter(isObj).filter(t => plainText(t.temperature) || plainText(t.hold)) }))
    .filter(x => x.steps.length);
  const ramps = stagesIn.flatMap(x => x.steps.map(t => plainText(t.ramp)));
  const ramp = ramps.length && ramps[0] && ramps.every(r => r === ramps[0]) ? ramps[0] : '';
  const stages = stagesIn.map(({ s, steps }, i) => {
    let name = pick(s, 'label');
    if (!name) {
      const n = fin(s.stage) ? s.stage : i + 1, kind = typeof s.kind === 'string' ? s.kind.trim() : '', repeats = fin(s.repeats) ? s.repeats : 0;
      name = [say('Stage {0}', n), kind].filter(Boolean).join(' · ') + (repeats > 1 ? ' × ' + repeats : '');
    }
    const program = steps.map(t => {
      let text = [plainText(t.temperature), plainText(t.hold)].filter(Boolean).join(' ');
      if (!ramp && plainText(t.ramp)) text = say('{0}, ramp {1}', text, plainText(t.ramp));
      return t.collect === true ? say('{0} (data collected)', text) : text;
    }).join(' → ');
    return say('{0}: {1}', name, program);
  });
  // the program gives the temperature program also as a section of rows (for the workbook): shown once, as the stages
  const shown = sections.filter(sec => !(stages.length && sec.own === 'Temperature program')).map(sec => ({ title: sec.title, rows: sec.rows }));
  if (!shown.length && !stages.length) return null;
  return { sections: shown, stages, ramp };
}

/* ---------- the PC and the program (GET /api/health) ---------- */
/** The checks of the PC in the language in use: [{ code, ok, text }]. */
function checkLines(health, zh) {
  return (isObj(health) && Array.isArray(health.checks) ? health.checks : []).filter(isObj)
    .map(c => ({ code: String(c.code || ''), ok: c.ok !== false, text: String((zh ? c.messageZh || c.message : c.message || c.messageZh) || c.code || '') }));
}
const EDITIONS = { installed: en('Installed, with the background service'), portable: en('Portable') };
const editionText = edition => (has(EDITIONS, edition) ? say(EDITIONS[edition]) : String(edition || ''));
/** The name of a file in a Content-Disposition header (filename*=UTF-8''... first). */
function fileNameOf(header) {
  const h = String(header || '');
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(h);
  if (star) { try { return decodeURIComponent(star[1].trim().replace(/^"|"$/g, '')); } catch (e) { /* the plain name */ } }
  const plain = /filename\s*=\s*"((?:[^"\\]|\\.)*)"/.exec(h) || /filename\s*=\s*([^;]+)/.exec(h);
  return plain ? plain[1].trim().replace(/\\(.)/g, '$1') : '';
}

const logic = { ZH, PROGRAM, PROGRAMS, ME, say: (fmt, ...args) => say(fmt, ...args), en, useWords, addChinese, chineseOf,
  TYPING_IDLE_MS, STATES, STATUS, HOLD, KINDS, EVENT_KINDS, REASONS, PROBLEMS, OWN_NAMES, plateOk, datasetId, usableCycles,
  kindOf, softwareOf, setScreenNames, reader, readerNames, runName, datasetName, experimentDatasetName, datasetInput, inProgressText,
  planChannel, differs, isEntry, holdReason, createFeed, endedEarly, cycleText, cyclesText, stateText, progressParts, progressText, tileParts, tileLive,
  temperatureText, timesText, recordCycles,
  runSeconds, stampMs, wallMs, localStamp, clockText, spanText, runTiming, quietSeconds, runSummary, newlyPositive, timeText,
  miniCurves, axisSteps, shortNumber, lookBackAt, lookBackPlace, lookBackStep, interruptionText, interruptionLine,
  searchWords, instrumentText, statusText, experimentList, experimentRows, experimentMeta, experimentStatus, experimentProblem, instrumentsOf, removedRows,
  omegaRows, omegaTitle, omegaMeta, omegaName, runOf, safeId, bytesText, storageProblem, placeOf, inFolder,
  problemLines, instrumentProblems, eventKey, newEvents, eventTitle, eventText, wanted, runOfDataset, isRunId, stableJson, analysisSig, hasSettings, analysisToSave,
  layoutNames, namesAnalysis, xlsxText, SYNC, textHash, revKey, versionOf, sameAnnotation, createSync, syncTexts, FOLD_MS, runStartMs, datasetEndMs, foldable,
  wellId, wellPos, plateWells, wellRange, newLayout, editLayout, layoutGroups, layoutMeta, settingsOf, nextText, checkLines, editionText, fileNameOf,
  protocolView };
if (typeof module === 'object' && module.exports) module.exports = logic;      // loaded by the tests
else window.SAALogic = logic;                                                 // in the page: for web/shell.js
})();
