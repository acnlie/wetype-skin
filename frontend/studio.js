(() => {
  'use strict';
  const $ = (selector) => document.querySelector(selector);
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const normalize = (theme) => ({ ...theme, imagePositionX: theme.imagePositionX ?? 0.5, imagePositionY: theme.imagePositionY ?? 0.5, imageZoom: theme.imageZoom ?? 1,
    petEnabled: theme.petEnabled ?? true, petCandidate: theme.petCandidate ?? true, petToolbar: theme.petToolbar ?? true, petAnimate: theme.petAnimate ?? true, petSize: theme.petSize ?? 88,
    petBackground: true, petStartWithWindows: theme.petStartWithWindows ?? false });
  const colors = [['background', '背景'], ['foreground', '文字'], ['accent', '强调色'], ['border', '辅助色']];
  const pending = new Map();
  let current, presets = [], applied = null, compatible = false, recoveryRequired = false, busy = false, operationError = null;
  let platform = window.studioPlatform?.platform || 'Windows';
  let nativeSupported = window.studioPlatform?.nativeSupported !== false, petSupported = window.studioPlatform?.petSupported !== false;
  let nativeVersion = '2.1.4.6';
  let cloneRunning = false;
  let inspectionError = null;
  let sequence = 0, revision = 0, savedRevision = 0, saveTimer, saveInFlight = false;
  let mode = 'native', history = [], historyIndex = -1, gestureStart = null, drag = null;
  let photo = null, photoData = null, imageGeneration = 0;
  const host = window.chrome?.webview || window.studioHost;
  let operationAction = null, operationTotal = 5;

  function request(action, theme) {
    if (!host) return Promise.reject(new Error('请通过皮肤工作室应用打开编辑器。'));
    const id = String(++sequence);
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      try { host.postMessage({ id, action, ...(theme ? { theme } : {}) }); }
      catch (error) { pending.delete(id); reject(error); }
    });
  }
  host?.addEventListener('message', ({ data }) => {
    if (data.id === 'pet-event') { petRuntime(data.pet || data.result?.pet); return; }
    if (data.id === 'installation-event') {
      syncInstallation(data.result);
      status(inspectionError || operationError || data.result?.message || '已刷新输入法状态', !!(inspectionError || operationError));
      return;
    }
    if (data.id === 'host-close') { syncInstallation(data.result); status(data.error, true); return; }
    if (data.progress) {
      if (operationAction === data.action) setOperationProgress(data.step, data.progressMessage, data.total);
      return;
    }
    const entry = pending.get(data.id);
    if (!entry) return;
    pending.delete(data.id);
    if (data.ok) entry.resolve(data.result);
    else { syncInstallation(data.result); entry.reject(new Error(data.error || '操作未完成。')); }
  });
  function status(message, error = false) {
    $('#status-message').textContent = message;
    $('#status-message').title = message;
    $('#status-message').classList.toggle('error', error);
  }
  function saveStatus(message, error = false) {
    $('#save-state').textContent = message;
    $('#save-state').classList.toggle('error', error);
  }
  const operationCopy = {
    apply: {
      title: '应用皮肤', kicker: '正在写入原生候选框', icon: 'sparkles',
      steps: [
        ['准备皮肤', '检查颜色、图片与构图'],
        ['等待授权', '请求 Windows 管理员权限'],
        ['写入原生文件', '安全停止输入法并提交变更'],
        ['重启输入法', '恢复官方 IPC 输入链路'],
        ['验证候选框', '确认输入连接和原生窗口']
      ], success: '文件已写入，输入法已启动。请在试打区检查实际效果。'
    },
    restore: {
      title: '还原官方', kicker: '正在恢复原生候选框', icon: 'rotate-ccw',
      steps: [
        ['准备官方还原', '核对备份与官方文件'],
        ['等待授权', '请求 Windows 管理员权限'],
        ['恢复官方文件', '安全停止输入法并还原文件'],
        ['重启输入法', '重新建立官方 IPC 输入链路'],
        ['验证官方状态', '确认候选窗口和输入连接']
      ], success: '已还原官方皮肤，输入连接已恢复。'
    }
  };
  function showOperation(action) {
    const copy = operationCopy[action];
    operationAction = action; operationTotal = copy.steps.length;
    const overlay = $('#operation-overlay'); overlay.hidden = false; overlay.dataset.action = action; overlay.dataset.state = 'running'; overlay.setAttribute('aria-hidden', 'false');
    $('#operation-title').textContent = copy.title; $('#operation-kicker-text').textContent = copy.kicker; $('#operation-icon').setAttribute('data-lucide', copy.icon);
    const steps = $('#operation-steps'); steps.replaceChildren();
    copy.steps.forEach((item, index) => {
      const row = document.createElement('div'); row.className = 'operation-step'; row.dataset.step = String(index + 1);
      const number = document.createElement('span'); number.className = 'step-index'; number.textContent = String(index + 1).padStart(2, '0');
      const text = document.createElement('span'); const title = document.createElement('b'); title.textContent = item[0]; const detail = document.createElement('small'); detail.textContent = item[1]; text.append(title, detail); row.append(number, text); steps.append(row);
    });
    $('#operation-state').textContent = '正在处理，请稍候'; $('#operation-icon').classList.remove('operation-icon-done');
    window.lucide.createIcons(); setOperationProgress(1, copy.steps[0][1], operationTotal);
  }
  function setOperationProgress(step, message, total) {
    if (!operationAction) return;
    operationTotal = Number(total) > 0 ? Number(total) : operationTotal;
    const currentStep = clamp(Number(step) || 1, 1, operationTotal);
    document.querySelectorAll('.operation-step').forEach((row) => {
      const value = Number(row.dataset.step); row.classList.toggle('active', value === currentStep); row.classList.toggle('complete', value < currentStep);
    });
    const percent = Math.max(8, Math.round(((currentStep - 1) / operationTotal) * 100));
    $('#operation-fill').style.width = percent + '%'; $('#operation-percent').textContent = percent + '%';
    $('#operation-count').textContent = String(currentStep).padStart(2, '0') + ' / ' + String(operationTotal).padStart(2, '0');
    if (message) $('#operation-message').textContent = message;
  }
  async function finishOperation(success, message) {
    if (!operationAction) return;
    const overlay = $('#operation-overlay'); overlay.dataset.state = success ? 'success' : 'error';
    if (success) {
      setOperationProgress(operationTotal, message || operationCopy[operationAction].success, operationTotal);
      $('#operation-fill').style.width = '100%'; $('#operation-percent').textContent = '100%';
      document.querySelectorAll('.operation-step').forEach((row) => { row.classList.add('complete'); row.classList.remove('active'); });
      $('#operation-state').textContent = '处理完成'; $('#operation-icon').setAttribute('data-lucide', 'check'); window.lucide.createIcons();
    } else {
      $('#operation-message').textContent = message || '操作未完成'; $('#operation-state').textContent = '操作失败';
      document.querySelectorAll('.operation-step').forEach((row) => row.classList.remove('active')); $('#operation-icon').setAttribute('data-lucide', 'triangle-alert'); window.lucide.createIcons();
    }
    await new Promise((resolve) => setTimeout(resolve, success ? 700 : 1100));
    overlay.hidden = true; overlay.setAttribute('aria-hidden', 'true'); overlay.dataset.state = ''; operationAction = null;
  }
  function valid() {
    return current && current.name?.trim() && current.name.length <= 80 && !document.querySelector('[aria-invalid="true"]') && colors.every(([key]) => /^#[0-9a-f]{6}$/i.test(current[key]));
  }
  function nativeSignature(theme) {
    if (!theme) return null;
    const image = theme.backgroundImage || '';
    // Avoid serializing an 8 MB image on every drag event while still detecting
    // replacement images in the native-application state.
    const imageFingerprint = image.length + ':' + image.slice(0, 48) + ':' + image.slice(-48);
    return JSON.stringify(colors.map(([key]) => theme[key].toUpperCase()).concat([
      theme.cornerRadius, theme.opacity, theme.imageTint, theme.imagePositionX, theme.imagePositionY, theme.imageZoom,
      imageFingerprint
    ]));
  }
  function updateActions() {
    const changed = nativeSignature(current) !== nativeSignature(applied);
    $('#apply').disabled = busy || !nativeSupported || !compatible || !valid();
    $('#apply span').textContent = !nativeSupported ? 'Mac 换肤待支持' : busy ? '处理中' : operationError ? '重试应用' : platform === 'macOS' ? '试验应用' : applied && !changed ? '重新应用' : '应用皮肤';
    $('#restore').disabled = busy || !nativeSupported || (!applied && !recoveryRequired);
    $('#export').disabled = busy || !valid();
    $('#import').disabled = busy;
    $('#properties').disabled = busy;
    $('#theme-name').disabled = busy;
    $('#undo').disabled = busy || historyIndex <= 0;
    $('#redo').disabled = busy || historyIndex >= history.length - 1;
    document.body.classList.toggle('busy', busy);
    $('#native-state').textContent = !nativeSupported ? 'Mac 端仅支持编辑、预览、保存和导出' : inspectionError ? '应用状态无法确认 · 请查看检测错误' : operationError ? '操作未完成' : recoveryRequired ? '候选框与工具条状态不一致 · 请先还原官方' : applied ? '已应用：' + applied.name + (changed ? ' · 修改待应用' : '') : '未应用候选框皮肤';
    $('.typing-panel').classList.toggle('applied', !!applied);
    $('#typing-status').textContent = nativeSupported ? (compatible ? '微信输入法 ' + nativeVersion : '原生应用不可用') : 'macOS · 官方输入法试打';
    $('.inspector-footer span').textContent = !nativeSupported ? 'macOS：可编辑和导出 · 原生应用待支持' : recoveryRequired ? '检测到部分应用 · 可还原官方' : compatible ? '原始文件已校验 · 支持还原' : '安装文件未通过校验';
    if (platform === 'macOS') {
      $('#restore').disabled = busy || !nativeSupported || (!applied && !cloneRunning && !recoveryRequired);
      $('#native-state').textContent = operationError ? '操作未完成 · 可以重试还原' : recoveryRequired ? '应用状态需要恢复 · 请还原官方' : applied ? changed ? '修改待应用' : cloneRunning ? '已应用 · 正在使用 WeType Skin' : '已应用 · 当前使用其他输入源' : '尚未应用皮肤';
      $('#typing-status').textContent = cloneRunning ? 'macOS · WeType Skin 试打' : 'macOS · 官方微信输入法试打';
      if (mode === 'native') $('#canvas-caption').textContent = cloneRunning ? '皮肤效果预览 · 下方使用 WeType Skin' : '皮肤效果预览 · 下方使用官方输入法';
      $('.inspector-footer span').textContent = '仅操作用户目录副本 · 官方包未修改';
    }
    document.body.dataset.platform = platform;
  }
  function syncInstallation(state) {
    if (!state) return;
    applied = state.applied; compatible = state.compatible; nativeSupported = state.nativeSupported !== false;
    petSupported = state.petSupported !== false; platform = state.platform || 'Windows'; nativeVersion = state.version || nativeVersion;
    recoveryRequired = !!state.recoveryRequired; operationError = state.operationError || null;
    inspectionError = state.inspectionError || null;
    cloneRunning = !!state.cloneRunning;
    petRuntime(state.pet);
    updateActions();
  }
  function configurePlatform() {
    if (platform !== 'macOS') return;
    operationCopy.apply = {
      title: '应用皮肤', kicker: '正在验证 Mac 输入法副本', icon: 'sparkles',
      steps: [
        ['准备皮肤', '检查颜色、图片与构图'],
        ['创建输入法副本', '复制官方包到用户输入法目录'],
        ['加载原生组件', '为副本加载候选框与工具条适配'],
        ['注册输入源', '请求 macOS 识别用户目录副本'],
        ['验证副本状态', '确认副本运行并保留官方输入法']
      ], success: '已切换到 WeType Skin，请试打核对皮肤效果。'
    };
    operationCopy.restore = {
      title: '还原官方', kicker: '正在恢复官方微信输入法', icon: 'rotate-ccw',
      steps: [
        ['准备还原', '核对当前皮肤与副本状态'],
        ['切回官方输入源', '确认官方微信输入法已选中'],
        ['停止皮肤副本', '官方输入源可用后再结束副本'],
        ['保留官方输入法', '确认官方包未被修改'],
        ['验证编辑器状态', '更新候选框与工具条状态']
      ], success: '副本已停止，官方微信输入法保持可用。'
    };
    $('.brand .version').textContent = 'Mac 编辑版';
    $('#connection').textContent = 'macOS · 本地离线';
    $('#mode-native').textContent = '皮肤预览';
    $('#apply').title = nativeSupported ? '启动 Mac 输入法副本并应用皮肤' : 'macOS 原生换肤组件未安装。';
    $('#restore').title = nativeSupported ? '停止 Mac 输入法副本并恢复官方输入法' : 'macOS 原生换肤组件未安装。';
    for (const badge of document.querySelectorAll('.scope.native')) badge.textContent = nativeSupported ? '试验' : '仅预览';
    $('.coverage h2').textContent = 'Mac 支持范围';
    const coverage = document.querySelectorAll('.coverage > div');
    coverage[0].querySelector('.scope').textContent = nativeSupported ? '待验证' : '可预览';
    coverage[1].querySelector('.scope').textContent = nativeSupported ? '待验证' : '未支持';
    $('.image-scope span').textContent = nativeSupported ? '图片、透明度和构图已支持编辑与保存。原生副本需通过 macOS 输入源注册，实际换肤效果尚待验证。' : '图片、透明度和构图只用于编辑器预览。导出可供支持的 Windows 版本使用。';
    $('#pet-panel h2').textContent = 'DeepSeek Chan 动作预览';
    $('#pet-enabled').closest('.pet-switch').hidden = false;
    $('#pet-startup').closest('label').hidden = true;
    $('#pet-runtime-state').textContent = petSupported ? '独立桌宠 · 编辑器退出后继续运行' : '动作预览 · 常驻桌宠待支持';
  }
  function checkpoint() {
    const previous = history[historyIndex];
    if (previous && JSON.stringify(previous) === JSON.stringify(current)) return;
    history = history.slice(0, historyIndex + 1);
    history.push({ ...current });
    if (history.length > 50) history.shift();
    historyIndex = history.length - 1;
    updateActions();
  }
  function changed(commit = true) {
    revision++;
    render();
    saveStatus('未保存');
    clearTimeout(saveTimer);
    if (commit) checkpoint();
    if (valid()) saveTimer = setTimeout(flushSave, 450);
    else { saveStatus('名称或颜色无效', true); status('填写有效的皮肤名称和颜色后才能保存。', true); }
  }
  async function flushSave() {
    clearTimeout(saveTimer);
    if (!valid() || busy || savedRevision === revision) return;
    if (saveInFlight) return;
    const target = revision;
    saveInFlight = true; saveStatus('正在保存');
    try {
      await request('save', { ...current });
      savedRevision = target;
      if (savedRevision === revision) saveStatus('已自动保存');
    } catch (error) { status(error.message, true); saveStatus('保存失败', true); }
    finally {
      saveInFlight = false;
      if (revision !== target && !busy) saveTimer = setTimeout(flushSave, 150);
    }
  }
  function setMode(value) {
    mode = value;
    $('#mode-native').classList.toggle('active', mode === 'native');
    $('#mode-image').classList.toggle('active', mode === 'image');
    $('#mode-native').setAttribute('aria-pressed', String(mode === 'native'));
    $('#mode-image').setAttribute('aria-pressed', String(mode === 'image'));
    $('#preview-region').classList.toggle('image-mode', mode === 'image');
    $('#preview-scope').className = 'scope native';
    $('#preview-scope').textContent = platform === 'macOS' ? '编辑器预览' : nativeSupported ? '原生候选框' : '编辑器预览';
    $('#mode-status').textContent = mode === 'native' ? '完整皮肤预览' : '拖动调整图片';
    $('#canvas-caption').textContent = mode === 'native' ? (nativeSupported && platform !== 'macOS' ? '皮肤效果预览 · 实际效果见下方试打' : '皮肤效果预览 · 下方使用官方输入法') : '候选框背景图片与构图';
    if (platform === 'macOS' && cloneRunning && mode === 'native') $('#canvas-caption').textContent = '皮肤效果预览 · 下方使用 WeType Skin';
    renderImageControls(); draw();
  }
  function setTab(value) {
    for (const tab of ['colors', 'image', 'pet']) {
      $('#tab-' + tab).classList.toggle('active', value === tab);
      $('#tab-' + tab).setAttribute('aria-selected', String(value === tab));
      $('#' + tab + '-panel').hidden = value !== tab;
    }
    setMode(value === 'image' ? 'image' : 'native');
    schedulePetPreview();
  }
  function createPresets() {
    const list = $('#presets'); list.replaceChildren();
    for (const preset of presets) {
      const button = document.createElement('button'); button.className = 'preset'; button.dataset.name = preset.name;
      const thumb = document.createElement('div'); thumb.className = 'preset-thumb';
      thumb.style.background = preset.background; thumb.style.color = preset.foreground;
      const first = document.createElement('span'); first.className = 'thumb-selected'; first.style.background = preset.accent; first.style.color = '#fff'; first.textContent = '1 你好';
      const second = document.createElement('span'); second.textContent = '2 拟好'; thumb.append(first, second);
      const meta = document.createElement('div'); meta.className = 'preset-meta';
      const name = document.createElement('span'); name.textContent = preset.name;
      const check = document.createElement('i'); check.dataset.lucide = 'check'; meta.append(name, check); button.append(thumb, meta);
      button.addEventListener('click', () => { if (busy) return; current = normalize(preset); changed(); status('已选择「' + current.name + '」'); });
      list.append(button);
    }
    window.lucide.createIcons();
  }
  function createColors() {
    for (const [key, label] of colors) {
      const container = document.createElement('div'); container.className = 'color-field';
      const caption = document.createElement('label'); caption.textContent = label; caption.htmlFor = key + '-hex';
      const control = document.createElement('div'); control.className = 'color-control';
      const swatch = document.createElement('div'); swatch.className = 'color-swatch'; swatch.id = key + '-swatch';
      const picker = document.createElement('input'); picker.type = 'color'; picker.id = key + '-color'; picker.setAttribute('aria-label', label + '颜色');
      picker.addEventListener('input', () => { current[key] = picker.value.toUpperCase(); changed(false); });
      picker.addEventListener('change', checkpoint);
      const hex = document.createElement('input'); hex.id = key + '-hex'; hex.className = 'hex-input'; hex.maxLength = 7; hex.spellcheck = false;
      hex.addEventListener('input', () => { if (/^#[0-9a-f]{6}$/i.test(hex.value)) { current[key] = hex.value.toUpperCase(); hex.removeAttribute('aria-invalid'); changed(false); } else { hex.setAttribute('aria-invalid', 'true'); updateActions(); } });
      hex.addEventListener('change', () => { if (/^#[0-9a-f]{6}$/i.test(hex.value)) checkpoint(); else { hex.value = current[key]; hex.removeAttribute('aria-invalid'); updateActions(); } });
      swatch.append(picker); control.append(swatch, hex); container.append(caption, control); $('#color-fields').append(container);
    }
  }
  function render() {
    if (!current) return;
    if (document.activeElement !== $('#theme-name')) $('#theme-name').value = current.name;
    for (const [key] of colors) {
      $('#' + key + '-color').value = current[key];
      if (document.activeElement !== $('#' + key + '-hex')) {
        $('#' + key + '-hex').value = current[key];
        $('#' + key + '-hex').removeAttribute('aria-invalid');
      }
      $('#' + key + '-swatch').style.background = current[key];
    }
    $('#radius').value = $('#radius-number').value = current.cornerRadius;
    $('#zoom').value = $('#zoom-number').value = $('#canvas-zoom').value = Math.round(current.imageZoom * 100);
    $('#canvas-zoom-value').textContent = Math.round(current.imageZoom * 100) + '%';
    $('#position-x').value = Math.round(current.imagePositionX * 100);
    $('#position-y').value = Math.round(current.imagePositionY * 100);
    $('#tint').value = Math.round(current.imageTint * 100); $('#tint-value').textContent = Math.round(current.imageTint * 100) + '%';
    $('#opacity').value = Math.round(current.opacity * 100); $('#opacity-value').textContent = Math.round(current.opacity * 100) + '%';
    for (const [id, key] of [['enabled', 'petEnabled'], ['candidate', 'petCandidate'], ['toolbar', 'petToolbar'], ['animate', 'petAnimate'], ['startup', 'petStartWithWindows']]) $('#pet-' + id).checked = current[key];
    $('#pet-size').value = $('#pet-size-number').value = current.petSize;
    const candidate = $('#candidate');
    candidate.style.setProperty('--candidate-bg', current.background); candidate.style.setProperty('--candidate-fg', current.foreground);
    candidate.style.setProperty('--candidate-accent', current.accent); candidate.style.setProperty('--candidate-aux', current.border);
    candidate.style.borderRadius = current.cornerRadius + 'px';
    for (const button of document.querySelectorAll('.preset')) {
      const preset = presets.find((item) => item.name === button.dataset.name);
      const selected = current.name === preset.name && nativeSignature(current) === nativeSignature(preset) && !current.backgroundImage;
      button.classList.toggle('selected', selected); button.setAttribute('aria-pressed', String(selected));
    }
    loadPhoto(); renderImageControls(); updateActions(); draw();
    schedulePetPreview();
  }
  function renderImageControls() {
    const hasImage = !!current?.backgroundImage;
    $('#image-asset').hidden = !hasImage;
    $('#image-toolbar').hidden = mode !== 'image' || !hasImage;
    $('#empty-image').hidden = mode !== 'image' || hasImage;
    $('#image-add span').textContent = hasImage ? '替换图片' : '选择图片';
    $('#image-name').textContent = current?.backgroundImageName || '背景图片';
    $('#image-name').title = current?.backgroundImageName || '';
    for (const element of document.querySelectorAll('.image-controls input, .image-controls button, #canvas-zoom, #canvas-reset')) element.disabled = !hasImage || busy;
  }
  function loadPhoto() {
    const data = current.backgroundImage || null;
    if (data === photoData) return;
    photoData = data; photo = null; imageGeneration++;
    if (!data) { $('#image-dimensions').textContent = ''; return; }
    const generation = imageGeneration;
    const image = new Image();
    image.onload = () => {
      if (generation !== imageGeneration) return;
      photo = image; $('#image-dimensions').textContent = image.naturalWidth + ' × ' + image.naturalHeight;
      const thumb = $('#image-thumb'), context = thumb.getContext('2d');
      context.clearRect(0, 0, thumb.width, thumb.height);
      const scale = Math.max(thumb.width / image.naturalWidth, thumb.height / image.naturalHeight);
      context.drawImage(image, (thumb.width - image.naturalWidth * scale) / 2, (thumb.height - image.naturalHeight * scale) / 2, image.naturalWidth * scale, image.naturalHeight * scale);
      draw();
    };
    image.onerror = () => { if (generation === imageGeneration) status('图片无法解码，请重新选择。', true); };
    image.src = 'data:image/' + (/\.jpe?g$/i.test(current.backgroundImageName || '') ? 'jpeg' : /\.gif$/i.test(current.backgroundImageName || '') ? 'gif' : /\.bmp$/i.test(current.backgroundImageName || '') ? 'bmp' : 'png') + ';base64,' + data;
  }
  function imageBounds(width, height) {
    const scale = Math.max(width / photo.naturalWidth, height / photo.naturalHeight) * current.imageZoom;
    const w = photo.naturalWidth * scale, h = photo.naturalHeight * scale;
    return { x: (width - w) * current.imagePositionX, y: (height - h) * current.imagePositionY, width: w, height: h };
  }
  function draw() {
    if (!current) return;
    const canvas = $('#background-canvas'), rect = $('#candidate').getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(rect.width * dpr); canvas.height = Math.round(rect.height * dpr);
    const context = canvas.getContext('2d'); context.scale(dpr, dpr);
    const dark = $('.workspace').classList.contains('dark-canvas');
    context.fillStyle = dark ? '#373a40' : '#f1f3f5'; context.fillRect(0, 0, rect.width, rect.height);
    const layer = document.createElement('canvas'); layer.width = canvas.width; layer.height = canvas.height;
    const paint = layer.getContext('2d'); paint.scale(dpr, dpr);
    paint.fillStyle = current.background; paint.fillRect(0, 0, rect.width, rect.height);
    if (photo) {
      const bounds = imageBounds(rect.width, rect.height);
      paint.drawImage(photo, bounds.x, bounds.y, bounds.width, bounds.height);
      paint.globalAlpha = current.imageTint;
      paint.fillStyle = current.background; paint.fillRect(0, 0, rect.width, rect.height);
    }
    context.globalAlpha = current.opacity;
    context.drawImage(layer, 0, 0, rect.width, rect.height);
    context.globalAlpha = 1;
  }
  function resetImage() { if (busy) return; current.imagePositionX = current.imagePositionY = 0.5; current.imageZoom = 1; changed(); }
  function bindRange(id, numberId, key, divisor) {
    const nodes = [$('#' + id), ...(numberId ? [$('#' + numberId)] : [])];
    for (const element of nodes) {
      element.addEventListener('input', () => {
        if (busy || element.value === '') return;
        const value = Number(element.value);
        if (!Number.isFinite(value)) return;
        current[key] = Math.round(clamp(value, Number(element.min), Number(element.max))) / divisor;
        changed(false);
      });
      element.addEventListener('change', () => { render(); checkpoint(); });
    }
  }
  async function acceptImage(file) {
    if (!file || busy) return;
    if (file.size > 8 * 1024 * 1024) { status('图片不能超过 8 MB。', true); return; }
    try {
      const data = await new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(String(reader.result)); reader.onerror = reject; reader.readAsDataURL(file); });
      const image = new Image(); image.src = data; await image.decode();
      if (image.naturalWidth > 4096 || image.naturalHeight > 4096) throw new Error('图片宽和高不能超过 4096 像素。');
      const next = { ...current, backgroundImage: data.slice(data.indexOf(',') + 1), backgroundImageName: file.name, imagePositionX: 0.5, imagePositionY: 0.5, imageZoom: 1 };
      await request('save', next);
      current = next; setTab('image'); changed(); status(nativeSupported && platform !== 'macOS' ? '已添加图片 · 应用后写入原生候选框' : '已添加图片 · 拖动调整预览构图');
    } catch (error) { status('无法添加图片：' + (error.message || '图片格式无效。'), true); }
  }
  async function nativeOperation(action) {
    if (busy) return;
    if (!nativeSupported) { status('macOS 当前支持编辑、预览、保存和导出，原生换肤尚未实现。', true); return; }
    if (action === 'apply' && (!compatible || !valid())) return;
    clearTimeout(saveTimer);
    if (saveInFlight) {
      status('皮肤正在保存，请稍后应用。'); return;
    }
    busy = true; updateActions(); renderImageControls();
    showOperation(action);
    status(platform === 'macOS' ? (action === 'apply' ? '正在验证输入法副本' : '正在停止输入法副本') : action === 'apply' ? '正在应用皮肤 · 输入法暂时锁定' : '正在还原官方文件 · 输入法暂时锁定');
    try {
      const state = await request(action, action === 'apply' ? { ...current } : undefined);
      syncInstallation(state);
      if (action === 'apply') { savedRevision = revision; saveStatus('已自动保存'); }
      const message = platform === 'macOS' ? state.message : action === 'restore' ? '还原文件操作完成，请试打检查输入。' : '皮肤文件已写入，请试打检查候选框图片和工具条的实际效果。';
      status(message); await finishOperation(true, operationCopy[action].success);
    } catch (error) { status(error.message, true); await finishOperation(false, error.message); }
    finally { busy = false; updateActions(); renderImageControls(); }
  }

  createColors(); window.lucide.createIcons();
  for (const site of ['github', 'x']) $('#project-' + site).addEventListener('click', async () => {
    try { await request('open-' + site); } catch (error) { status(error.message, true); }
  });
  $('#theme-name').addEventListener('input', () => { current.name = $('#theme-name').value; changed(false); });
  $('#theme-name').addEventListener('change', checkpoint);
  $('#mode-native').addEventListener('click', () => setMode('native'));
  $('#mode-image').addEventListener('click', () => setMode('image'));
  $('#tab-colors').addEventListener('click', () => setTab('colors'));
  $('#tab-image').addEventListener('click', () => setTab('image'));
  $('#tab-pet').addEventListener('click', () => setTab('pet'));
  for (const [id, key] of [['enabled', 'petEnabled'], ['candidate', 'petCandidate'], ['toolbar', 'petToolbar'], ['animate', 'petAnimate'], ['startup', 'petStartWithWindows']])
    $('#pet-' + id).addEventListener('change', () => { current[key] = $('#pet-' + id).checked; changed(); });
  bindRange('pet-size', 'pet-size-number', 'petSize', 1);
  const petNames = { idle: '待机', typing: '输入', waiting: '候选停留', navigating: '选词与翻页', committed: '确认上屏', cancelled: '取消输入', mode: '转身', sleeping: '休息',
    'mode-chinese': '中文举牌', 'mode-english': 'English 举牌', 'mode-half': '半角举牌', 'mode-full': '全角举牌', 'punctuation-chinese': '中文标点', 'punctuation-english': 'English 标点', punctuation: '标点切换', rice: '吃饭', tea: '喝奶茶', stretch: '伸懒腰', humming: '哼歌', bubbles: '吐泡泡', cube: '玩魔方', thinking: '思考', watch: '看看时间', tapping: '敲敲桌面', yawn: '打哈欠', sleepy: '睡眼惺忪', sign: '举牌' };
  const petSheets = new Map();
  let petPreviewState = 'idle', petPreviewStart = performance.now();
  function petSheet(name) {
    if (!petSheets.has(name) && window.petAssets?.[name]) {
      if (petSheets.size >= 3) {
        const oldest = petSheets.keys().next().value, cached = petSheets.get(oldest);
        cached.onload = cached.onerror = null; cached.src = ''; petSheets.delete(oldest);
      }
      const image = new Image(); petSheets.set(name, image);
      image.onload = () => { if (petSheets.get(name) === image) schedulePetPreview(); };
      image.onerror = () => {
        if (petSheets.get(name) !== image || petPreviewState !== name) return;
        $('#pet-preview-name').textContent = '动画加载失败';
        $('#pet-preview-name').classList.add('error');
        schedulePetPreview();
      };
      image.src = window.petAssets[name].url;
    }
    return petSheets.get(name);
  }
  function petRuntime(runtime) {
    if (!petSupported) { $('#pet-runtime-state').textContent = '动作预览 · 常驻桌宠待支持'; return; }
    if (!runtime) return;
    const macStates = { stopped: '桌宠未运行 · 重新打开工作室可启动', starting: '桌宠正在启动', hidden: '桌宠运行中 · 等待候选框', visible: '桌宠正在跟随候选框' };
    $('#pet-runtime-state').textContent = runtime.error || (!runtime.enabled ? '已关闭' : platform === 'macOS' ? macStates[runtime.state] || '桌宠运行中' : '实时 · ' + (petNames[runtime.motion] || petNames[runtime.state] || '待机'));
    $('#pet-runtime-state').classList.toggle('error', !!runtime.error);
  }
  $('#pet-motion').addEventListener('change', () => {
    petPreviewState = $('#pet-motion').value; petPreviewStart = performance.now();
    $('#pet-preview-name').textContent = petNames[petPreviewState] || petPreviewState;
    $('#pet-preview-name').classList.remove('error');
    schedulePetPreview();
  });
  let petPreviewFrame = null;
  function schedulePetPreview() {
    if (petPreviewFrame !== null) cancelAnimationFrame(petPreviewFrame);
    petPreviewFrame = null;
    if (current && !document.hidden && !$('#pet-panel').hidden) petPreviewFrame = requestAnimationFrame(drawPet);
  }
  function drawPet(now) {
    petPreviewFrame = null;
    if (current && !document.hidden && !$('#pet-panel').hidden) {
      const canvas = $('#pet-preview'), context = canvas.getContext('2d'), clip = window.petAssets?.[petPreviewState], image = petSheet(petPreviewState);
      context.clearRect(0, 0, canvas.width, canvas.height);
      if (clip && image?.complete && image.naturalWidth) {
        const frame = current.petAnimate ? Math.floor((now - petPreviewStart) / 1000 * clip.fps) % clip.frames : 0;
        const size = Math.min(150, current.petSize * 1.3);
        context.drawImage(image, frame % clip.columns * clip.size, Math.floor(frame / clip.columns) * clip.size, clip.size, clip.size,
          (canvas.width - size) / 2, canvas.height - size, size, size);
        $('#pet-preview-name').textContent = petNames[petPreviewState] || petPreviewState;
        $('#pet-preview-name').classList.remove('error');
      } else if (clip && image?.complete && !image.naturalWidth) {
        $('#pet-preview-name').textContent = '动画加载失败';
        $('#pet-preview-name').classList.add('error');
      }
      if (clip && image && (!image.complete || image.naturalWidth && current.petAnimate)) petPreviewFrame = requestAnimationFrame(drawPet);
    }
  }
  document.addEventListener('visibilitychange', schedulePetPreview);
  window.studioPetState = () => request('pet-state');
  $('#canvas-light').addEventListener('click', () => canvasTheme(false));
  $('#canvas-dark').addEventListener('click', () => canvasTheme(true));
  function canvasTheme(dark) {
    $('.workspace').classList.toggle('dark-canvas', dark);
    $('#canvas-light').classList.toggle('active', !dark); $('#canvas-dark').classList.toggle('active', dark);
    $('#canvas-light').setAttribute('aria-pressed', String(!dark)); $('#canvas-dark').setAttribute('aria-pressed', String(dark)); draw();
  }
  bindRange('radius', 'radius-number', 'cornerRadius', 1);
  bindRange('zoom', 'zoom-number', 'imageZoom', 100);
  bindRange('canvas-zoom', null, 'imageZoom', 100);
  bindRange('position-x', null, 'imagePositionX', 100);
  bindRange('position-y', null, 'imagePositionY', 100);
  bindRange('tint', null, 'imageTint', 100);
  bindRange('opacity', null, 'opacity', 100);
  $('#image-reset').addEventListener('click', resetImage); $('#canvas-reset').addEventListener('click', resetImage);
  async function chooseNative(action) {
    if (busy || (action === 'image' && !valid())) return;
    clearTimeout(saveTimer);
    // A pending automatic save must finish before a native file panel can
    // replace current, otherwise its stale response can undo the import.
    if (saveInFlight) { status('皮肤正在保存，请稍后打开文件。'); return; }
    busy = true; updateActions();
    try {
      const state = await request(action, action === 'image' ? { ...current } : undefined);
      if (!state.cancelled) {
        current = normalize(state.current); changed();
        if (action === 'image') setTab('image');
        status(action === 'image' ? '已添加图片 · 拖动调整预览构图' : '已导入「' + current.name + '」');
      }
    } catch (error) { status(error.message, true); }
    finally { busy = false; updateActions(); renderImageControls(); if (revision !== savedRevision) saveTimer = setTimeout(flushSave, 150); }
  }
  function chooseImage() { if (platform === 'macOS') chooseNative('image'); else $('#image-file').click(); }
  $('#image-add').addEventListener('click', chooseImage); $('#empty-image-add').addEventListener('click', chooseImage);
  $('#image-file').addEventListener('change', async () => { await acceptImage($('#image-file').files[0]); $('#image-file').value = ''; });
  $('#image-remove').addEventListener('click', () => { delete current.backgroundImage; delete current.backgroundImageName; current.imagePositionX = current.imagePositionY = 0.5; current.imageZoom = 1; changed(); });
  $('#clear-typing').addEventListener('click', () => { $('#typing').value = ''; $('#typing').focus(); });
  $('#import').addEventListener('click', () => { if (platform === 'macOS') chooseNative('import'); else $('#import-file').click(); });
  $('#import-file').addEventListener('change', async () => {
    const file = $('#import-file').files[0]; $('#import-file').value = '';
    if (!file) return;
    try {
      if (file.size > 13 * 1024 * 1024) throw new Error('皮肤文件过大。');
      const next = normalize(JSON.parse(await file.text())); const state = await request('save', next);
      current = normalize(state.current); changed(); status('已导入「' + current.name + '」');
    } catch (error) { status('导入失败：' + error.message, true); }
  });
  $('#export').addEventListener('click', async () => {
    if (busy) return;
    if (platform === 'macOS' && saveInFlight) { status('皮肤正在保存，请稍后导出。'); return; }
    if (platform === 'macOS') { clearTimeout(saveTimer); busy = true; updateActions(); }
    try { const state = await request('export', { ...current }); if (!state.cancelled) status('已导出皮肤，包含图片和构图位置。'); }
    catch (error) { status(error.message, true); }
    finally {
      if (platform === 'macOS') { busy = false; updateActions(); if (revision !== savedRevision) saveTimer = setTimeout(flushSave, 150); }
    }
  });
  $('#apply').addEventListener('click', () => nativeOperation('apply'));
  $('#restore').addEventListener('click', () => nativeOperation('restore'));
  function undo(direction) {
    if (busy || historyIndex + direction < 0 || historyIndex + direction >= history.length) return;
    historyIndex += direction; current = { ...history[historyIndex] }; changed(false);
  }
  $('#undo').addEventListener('click', () => undo(-1)); $('#redo').addEventListener('click', () => undo(1));
  document.addEventListener('keydown', (event) => {
    if (!(event.ctrlKey || event.metaKey) || /input|textarea/i.test(event.target.tagName)) return;
    if (event.key.toLowerCase() === 'z') { event.preventDefault(); undo(event.shiftKey ? 1 : -1); }
    else if (event.key.toLowerCase() === 'y') { event.preventDefault(); undo(1); }
  });
  $('#candidate').addEventListener('pointerdown', (event) => {
    if (mode !== 'image' || !photo || busy || event.button !== 0) return;
    const rect = $('#candidate').getBoundingClientRect(), bounds = imageBounds(rect.width, rect.height);
    drag = { id: event.pointerId, x: event.clientX, y: event.clientY, positionX: current.imagePositionX, positionY: current.imagePositionY, overflowX: bounds.width - rect.width, overflowY: bounds.height - rect.height };
    gestureStart = { ...current };
    $('#candidate').setPointerCapture(event.pointerId); $('#candidate').classList.add('dragging'); event.preventDefault();
  });
  $('#candidate').addEventListener('pointermove', (event) => {
    if (!drag || drag.id !== event.pointerId) return;
    current.imagePositionX = drag.overflowX > 0.1 ? clamp(drag.positionX - (event.clientX - drag.x) / drag.overflowX, 0, 1) : drag.positionX;
    current.imagePositionY = drag.overflowY > 0.1 ? clamp(drag.positionY - (event.clientY - drag.y) / drag.overflowY, 0, 1) : drag.positionY;
    changed(false);
  });
  function finishDrag(event) {
    if (!drag || drag.id !== event.pointerId) return;
    drag = null; gestureStart = null; $('#candidate').classList.remove('dragging'); checkpoint();
  }
  $('#candidate').addEventListener('pointerup', finishDrag);
  $('#candidate').addEventListener('lostpointercapture', finishDrag);
  $('#candidate').addEventListener('pointercancel', (event) => { if (gestureStart) { current = gestureStart; changed(false); } finishDrag(event); });
  $('#candidate').addEventListener('wheel', (event) => {
    if (mode !== 'image' || !photo || busy) return;
    event.preventDefault(); current.imageZoom = clamp(current.imageZoom + (event.deltaY < 0 ? 0.05 : -0.05), 1, 4); changed();
  }, { passive: false });
  $('#candidate').addEventListener('keydown', (event) => {
    if (mode !== 'image' || !photo || busy) return;
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
    event.preventDefault();
    const step = event.shiftKey ? 0.1 : 0.01;
    if (event.key === 'ArrowLeft') current.imagePositionX = clamp(current.imagePositionX + step, 0, 1);
    if (event.key === 'ArrowRight') current.imagePositionX = clamp(current.imagePositionX - step, 0, 1);
    if (event.key === 'ArrowUp') current.imagePositionY = clamp(current.imagePositionY + step, 0, 1);
    if (event.key === 'ArrowDown') current.imagePositionY = clamp(current.imagePositionY - step, 0, 1);
    changed();
  });
  let dropDepth = 0;
  $('#preview-region').addEventListener('dragenter', (event) => { if ([...event.dataTransfer.types].includes('Files')) { event.preventDefault(); dropDepth++; $('#drop-target').hidden = false; } });
  $('#preview-region').addEventListener('dragover', (event) => { if ([...event.dataTransfer.types].includes('Files')) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; } });
  $('#preview-region').addEventListener('dragleave', () => { dropDepth = Math.max(0, dropDepth - 1); if (!dropDepth) $('#drop-target').hidden = true; });
  $('#preview-region').addEventListener('drop', (event) => { event.preventDefault(); dropDepth = 0; $('#drop-target').hidden = true; acceptImage(event.dataTransfer.files[0]); });
  new ResizeObserver(draw).observe($('#candidate'));
  window.addEventListener('beforeunload', () => { if (platform !== 'macOS' && valid() && revision !== savedRevision) host?.postMessage({ id: 'last-save', action: 'save', theme: current }); });
  window.studioSnapshot = () => valid() ? { ...current } : null;
  window.studioPrepareClose = () => {
    clearTimeout(saveTimer); busy = true; updateActions();
    return window.studioSnapshot();
  };
  window.studioCancelClose = () => { busy = false; updateActions(); renderImageControls(); };
  window.studioDisablePet = () => {
    if (!current || busy || !valid()) return null;
    current.petEnabled = false; changed();
    return window.studioSnapshot();
  };
  async function initialize() {
    try {
      const state = await request('bootstrap'); current = normalize(state.current); presets = state.presets.map(normalize);
      createPresets(); syncInstallation(state); configurePlatform(); render(); checkpoint(); setMode('native'); saveStatus('已加载');
      status(inspectionError || operationError || state.message || (state.applied ? '检测到已应用皮肤：' + state.applied.name : '编辑后可应用皮肤'), !!(inspectionError || operationError));
    } catch (error) { status(error.message, true); saveStatus('连接失败', true); }
  }
  configurePlatform();
  initialize();
})();
