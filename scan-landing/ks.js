/* KillerScan site - shared chrome behavior (theme, accent, language, easter egg).
   Page-specific behavior (screenshot strip, outline scroll-spy) stays inline per page. */
(function () {
  var root = document.documentElement;
  var THEMES = ['dark','light','hc','blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'];
  var NEUTRAL = ['dark','light','hc'];
  var THEMED = ['blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'];  // fixed-color wordmark art
  // Per-family palette copied from the app: [ Accent (bright: text/links/logo/outlines), SelectionBg (darker fill: solid buttons, selected tab edges) ].
  var ACCENTS = {
    dark:  { red:['#DD504B','#5E1C1C'], orange:['#E8962C','#F29A28'], green:['#1EA54C','#1C5E38'], teal:['#1FB8A8','#1C5E5C'], blue:['#50AEE8','#1C3B5E'], purple:['#B982E3','#411C5E'] },
    light: { red:['#931A1A','#931A1A'], orange:['#C7710F','#C7710F'], green:['#1B5E20','#1B5E20'], teal:['#0D827E','#0D827E'], blue:['#18608E','#18608E'], purple:['#5A1690','#5A1690'] },
    hc:    { red:['#FF2929','#FF2929'], orange:['#FF910A','#FF910A'], green:['#00FF66','#00FF66'], teal:['#0AFFE7','#0AFFE7'], blue:['#298DFF','#298DFF'], purple:['#B829FF','#B829FF'] }
  };
  // SelectionBg (muted accent) for inactive tab/card edges; brightens to accent on hover. Mirrors KillerTools themes.ts.
  var SEL = {
    dark:  { red:'#5E1C1C', orange:'#5E3B16', green:'#1C5E38', teal:'#1C5E5C', blue:'#1C3B5E', purple:'#411C5E' },
    light: { red:'#931A1A', orange:'#C7710F', green:'#1B5E20', teal:'#0D827E', blue:'#18608E', purple:'#5A1690' },
    hc:    { red:'#380000', orange:'#4E2900', green:'#003314', teal:'#003832', blue:'#0A2C50', purple:'#250038' }
  };
  function famFor(t) { return t === 'light' ? 'light' : t === 'hc' ? 'hc' : 'dark'; }

  var swatches = [].slice.call(document.querySelectorAll('.swatch'));
  var accDots  = [].slice.call(document.querySelectorAll('.acc'));
  var accentSwitch = document.getElementById('accentSwitch');
  var accToggle = document.getElementById('accentToggle');
  var accPop = document.getElementById('accentPop');
  var curAccent = 'orange';

  function buildThemeFlyout() {
    var group = document.querySelector('.topbar .tgrp');
    if (!group || !group.parentNode) return;
    var toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'theme-toggle';
    toggle.title = 'Theme';
    toggle.setAttribute('aria-label', 'Choose theme');
    toggle.setAttribute('aria-haspopup', 'true');
    toggle.setAttribute('aria-expanded', 'false');
    var preview = document.createElement('span');
    preview.setAttribute('aria-hidden', 'true');
    toggle.appendChild(preview);
    group.parentNode.insertBefore(toggle, group);
    function closeFlyout(focusToggle) {
      group.classList.remove('open');
      toggle.setAttribute('aria-expanded', 'false');
      if (focusToggle) toggle.focus();
    }
    function syncPreview(name) {
      var active = group.querySelector('.swatch[data-theme="' + name + '"]') || group.querySelector('.swatch');
      if (active) preview.className = active.className;
      preview.removeAttribute('aria-pressed');
    }
    toggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var opening = !group.classList.contains('open');
      group.classList.toggle('open', opening);
      toggle.setAttribute('aria-expanded', opening ? 'true' : 'false');
    });
    group.addEventListener('click', function (e) {
      var swatch = e.target.closest('.swatch[data-theme]');
      if (!swatch) return;
      syncPreview(swatch.getAttribute('data-theme'));
      closeFlyout(false);
    });
    document.addEventListener('click', function (e) {
      if (!group.contains(e.target) && !toggle.contains(e.target)) closeFlyout(false);
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && group.classList.contains('open')) closeFlyout(true);
    });
    syncPreview(root.getAttribute('data-theme') || 'dark');
  }
  buildThemeFlyout();

  function applyAccent(name) {
    var theme = root.getAttribute('data-theme');
    var fam = famFor(theme);
    if (!ACCENTS[fam][name]) name = 'green';
    ['dark', 'light', 'hc'].forEach(function (neutralTheme) {
      var preview = ACCENTS[neutralTheme][name];
      if (preview) document.querySelectorAll('.sw-' + neutralTheme).forEach(function (dot) {
        dot.style.setProperty('--sw-accent', preview[0]);
      });
    });
    curAccent = name;
    var pair = ACCENTS[fam][name];
    var neutral = NEUTRAL.indexOf(theme) >= 0;
    if (neutral) {
      root.style.setProperty('--accent', pair[0]);
      root.style.setProperty('--btn', pair[1]);
      root.style.setProperty('--sel', (SEL[fam] && SEL[fam][name]) || pair[1]);
      try { localStorage.setItem('kscan-av', pair[0] + '|' + pair[1]); } catch (e) {}
    } else {
      root.style.removeProperty('--accent');
      root.style.removeProperty('--btn');
      root.style.removeProperty('--sel');
    }
    accDots.forEach(function (d) {
      var p = ACCENTS[fam][d.dataset.accent];
      if (p) { d.style.background = p[0]; d.style.color = p[0]; }
      d.setAttribute('aria-pressed', d.dataset.accent === name ? 'true' : 'false');
    });
    if (accToggle) { accToggle.style.background = pair[0]; accToggle.title = uiText('ui_accent'); }
    try { localStorage.setItem('kscan-accent', name); } catch (e) {}
    updateLogos();
  }
  function updateLogos() {
    var theme = root.getAttribute('data-theme');
    var src;
    if (THEMED.indexOf(theme) >= 0) {
      // Fixed-color themes carry their own wordmark art, colored with the theme's in-app
      // PrimaryBrush (make-logo-svgs.py --themes).
      src = 'brand/killerscan-logo-' + theme + '.svg';
    } else {
      var variant = (theme === 'light') ? 'light' : 'dark';
      var color = (NEUTRAL.indexOf(theme) >= 0) ? curAccent : 'orange';
      src = 'brand/killerscan-logo-' + variant + '-' + color + '.svg';
    }
    var imgs = document.querySelectorAll('img.wm-logo');
    for (var i = 0; i < imgs.length; i++) imgs[i].src = src;
  }

  function setTheme(name) {
    if (THEMES.indexOf(name) < 0) name = 'dark';
    root.setAttribute('data-theme', name);
    try { localStorage.setItem('kscan-theme', name); } catch (e) {}
    swatches.forEach(function (s) { s.setAttribute('aria-pressed', s.dataset.theme === name ? 'true' : 'false'); });
    if (accentSwitch) accentSwitch.hidden = NEUTRAL.indexOf(name) < 0;
    applyAccent(curAccent);
  }

  swatches.forEach(function (s) { s.addEventListener('click', function () { setTheme(s.dataset.theme); if (NEUTRAL.indexOf(s.dataset.theme) >= 0) showAccentBar(); else hideAccentBar(); }); });
  // Build a drop-down accent bar under the toolbar (moves the swatches out of the small header popup).
  var accentBar = null;
  var topbarEl = document.querySelector('.topbar');
  if (topbarEl && accDots.length) {
    accentBar = document.createElement('div');
    accentBar.className = 'accent-bar';
    var pill = document.createElement('div'); pill.className = 'pill';
    var grip = document.createElement('span'); grip.className = 'grip'; grip.setAttribute('aria-hidden', 'true');
    pill.appendChild(grip);
    var blbl = document.createElement('span'); blbl.className = 'lbl'; blbl.setAttribute('data-i18n', 'accent_label'); blbl.textContent = 'accent:';
    pill.appendChild(blbl);
    accDots.forEach(function (d) { pill.appendChild(d); });
    var bx = document.createElement('button'); bx.className = 'x'; bx.setAttribute('aria-label', 'Close'); bx.innerHTML = '&times;';
    bx.addEventListener('click', hideAccentBar);
    pill.appendChild(bx);
    accentBar.appendChild(pill);
    topbarEl.parentNode.insertBefore(accentBar, topbarEl.nextSibling);
    if (accPop) accPop.remove();

    // Drag the strip sideways by its grip, clamped so it stays inside the content pane (the frame).
    var dragDx = 0, dragging = false, dragStartX = 0, dragStartDx = 0;
    function dragClamp(v) {
      var vw = window.innerWidth, pw = pill.offsetWidth, pad = 6, left = 8, right = vw - 8;
      var f = document.querySelector('.content');
      // Extra inset on the right so the pill clears the content scrollbar at its max position.
      if (f) { var fr = f.getBoundingClientRect(); if (fr.width > 0) { left = fr.left + pad; right = fr.right - pad - 12; } }
      var centerLeft = vw / 2 - pw / 2, min = left - centerLeft, max = right - pw - centerLeft;
      if (min > max) return 0;
      return Math.max(min, Math.min(max, v));
    }
    grip.addEventListener('mousedown', function (e) {
      dragging = true; dragStartX = e.clientX; dragStartDx = dragDx;
      document.body.style.userSelect = 'none'; e.preventDefault();
    });
    window.addEventListener('mousemove', function (e) {
      if (!dragging) return;
      dragDx = dragClamp(dragStartDx + (e.clientX - dragStartX));
      pill.style.transform = 'translateX(' + dragDx + 'px)';
    });
    window.addEventListener('mouseup', function () {
      if (!dragging) return; dragging = false; document.body.style.userSelect = '';
    });
    function dockAccentBar() {
      var contentPane = document.querySelector('.content');
      if (contentPane) accentBar.style.top = Math.round(contentPane.getBoundingClientRect().top) + 'px';
    }
    window.addEventListener('resize', function () { dockAccentBar(); dragDx = dragClamp(dragDx); pill.style.transform = 'translateX(' + dragDx + 'px)'; });
    // Default position: top-right corner, nearest the theme picker (still draggable from there).
    requestAnimationFrame(function () { dragDx = dragClamp(1e6); pill.style.transform = 'translateX(' + dragDx + 'px)'; });
  }
  function showAccentBar() { if (accentBar && NEUTRAL.indexOf(root.getAttribute('data-theme')) >= 0) { var contentPane = document.querySelector('.content'); if (contentPane) accentBar.style.top = Math.round(contentPane.getBoundingClientRect().top) + 'px'; accentBar.classList.add('show'); if (accToggle) accToggle.setAttribute('aria-expanded', 'true'); } }
  function hideAccentBar() { if (accentBar) { accentBar.classList.remove('show'); if (accToggle) accToggle.setAttribute('aria-expanded', 'false'); } }
  accDots.forEach(function (d) { d.addEventListener('click', function () { applyAccent(d.dataset.accent); }); });
  if (accToggle) {
    accToggle.addEventListener('click', function (e) { e.stopPropagation(); if (accentBar && accentBar.classList.contains('show')) hideAccentBar(); else showAccentBar(); });
  }
  document.addEventListener('click', function (e) { if (accentBar && accentBar.classList.contains('show') && !e.target.closest('.accent-bar') && !e.target.closest('#accentToggle')) hideAccentBar(); });

  // Localized visible text, accessible names, metadata, and generated controls.
  var I18N = (typeof window !== 'undefined' && window.I18N) ? window.I18N : {};
  var EN = {
    "ui_home": "KillerScan home",
    "ui_theme": "Theme",
    "ui_accent": "Accent color",
    "ui_language": "Language",
    "ui_click_me": "click me",
    "ui_screenshot_expanded": "Expanded KillerScan screenshot",
    "ui_cli_examples": "KillerScan command examples",
    "ui_terminal_examples": "KillerScan terminal command examples",
    "ui_carousel": "carousel",
    "ui_slide": "slide",
    "ui_features": "Product features",
    "ui_previous_feature": "Previous feature",
    "ui_next_feature": "Next feature",
    "ui_choose_feature": "Choose a feature",
    "ui_feature": "Feature",
    "ui_base": "BASE",
    "ui_part_of": "Part of",
    "ui_report_issue": "Report an issue",
    "ui_email": "Email",
    "ui_coffee": "Buy me a coffee",
    "ui_version": "version",
    "ui_released": "released",
    "ui_size": "size",
    "ui_platform": "platform",
    "ui_egg": "No packets were harmed in the scanning of this network.",
    "ui_cmd_wait": "REM Command Prompt: wait, then read %ERRORLEVEL%",
    "ui_ps_wait": "# PowerShell: wait for completion",
    "ui_close": "Close",
    "ui_choose_theme": "Choose theme",
    "ui_theme_dark": "Dark",
    "ui_theme_light": "Light",
    "ui_theme_hc": "Black",
    "ui_theme_98se": "98SE",
    "ui_theme_blood": "Blood",
    "ui_theme_greed": "Greed",
    "ui_theme_cyanotic": "Cyanotic",
    "ui_theme_ectoplasm": "Ectoplasm",
    "ui_theme_decay": "Decay",
    "ui_theme_malaise": "Malaise",
    "ui_theme_sepulchre": "Sepulchre",
    "ui_theme_delirium": "Delirium",
    "ui_theme_mourning": "Mourning",
    "ui_accent_red": "Red",
    "ui_accent_orange": "Orange",
    "ui_accent_green": "Green",
    "ui_accent_teal": "Teal",
    "ui_accent_blue": "Blue",
    "ui_accent_purple": "Purple"
  };
  var currentLang = 'en';
  var translatedAttributes = ['title', 'aria-label', 'aria-roledescription', 'content'];
  function bindLabel(selector, attribute, key) {
    document.querySelectorAll(selector).forEach(function (node) {
      node.setAttribute('data-i18n-' + attribute, key);
    });
  }
  bindLabel('.tb-home', 'title', 'ui_home');
  bindLabel('.tgrp, .theme-toggle', 'aria-label', 'ui_choose_theme');
  bindLabel('.theme-toggle', 'title', 'ui_theme');
  bindLabel('#accentToggle, #accentPop', 'title', 'ui_accent');
  bindLabel('#accentToggle, #accentPop', 'aria-label', 'ui_accent');
  bindLabel('.lang-switch, #langToggle', 'aria-label', 'ui_language');
  bindLabel('#langToggle', 'title', 'ui_language');
  bindLabel('#verEgg', 'title', 'ui_click_me');
  bindLabel('#lightbox', 'aria-label', 'ui_screenshot_expanded');
  bindLabel('.accent-bar .x', 'aria-label', 'ui_close');
  document.querySelectorAll('.swatch[data-theme]').forEach(function (node) {
    var key = 'ui_theme_' + node.getAttribute('data-theme');
    node.setAttribute('data-i18n-title', key);
    node.setAttribute('data-i18n-aria-label', key);
  });
  document.querySelectorAll('.acc[data-accent]').forEach(function (node) {
    var key = 'ui_accent_' + node.getAttribute('data-accent');
    node.setAttribute('data-i18n-title', key);
    node.setAttribute('data-i18n-aria-label', key);
  });
  document.querySelectorAll('.cli-demo[aria-label]').forEach(function (node) {
    node.setAttribute('data-i18n-aria-label', node.getAttribute('aria-label').indexOf('terminal') >= 0 ? 'ui_terminal_examples' : 'ui_cli_examples');
  });
  document.querySelectorAll('[data-i18n]').forEach(function (node) {
    EN[node.getAttribute('data-i18n')] = node.innerHTML;
  });
  translatedAttributes.forEach(function (attribute) {
    document.querySelectorAll('[data-i18n-' + attribute + ']').forEach(function (node) {
      var key = node.getAttribute('data-i18n-' + attribute);
      if (EN[key] == null) EN[key] = node.getAttribute(attribute);
    });
  });
  function uiText(key) {
    var dict = currentLang === 'en' ? EN : I18N[currentLang];
    return dict && dict[key] != null ? dict[key] : EN[key];
  }
  function translateAttributes() {
    translatedAttributes.forEach(function (attribute) {
      document.querySelectorAll('[data-i18n-' + attribute + ']').forEach(function (node) {
        var value = uiText(node.getAttribute('data-i18n-' + attribute));
        if (value != null) node.setAttribute(attribute, value);
      });
    });
  }
  function translateCarousel() {
    var selectors = [
      ['.feature-carousel', 'aria-roledescription', 'ui_carousel'],
      ['.feature-carousel', 'aria-label', 'ui_features'],
      ['.feature-carousel-arrow:first-child', 'aria-label', 'ui_previous_feature'],
      ['.feature-carousel-arrow:last-child', 'aria-label', 'ui_next_feature'],
      ['.feature-carousel-rail', 'aria-label', 'ui_choose_feature'],
      ['.feature-carousel .feature-card', 'aria-roledescription', 'ui_slide']
    ];
    selectors.forEach(function (item) {
      document.querySelectorAll(item[0]).forEach(function (node) {
        node.setAttribute(item[1], uiText(item[2]));
      });
    });
  }
  function fitDiagramLabels() {
    document.querySelectorAll('svg text[data-i18n-max-width]').forEach(function (node) {
      node.removeAttribute('textLength');
      node.removeAttribute('lengthAdjust');
      var width = Number(node.getAttribute('data-i18n-max-width'));
      var advance = node.getComputedTextLength();
      var bounds = Math.max(advance, node.getBBox().width);
      if (width > 0 && bounds > width) {
        node.setAttribute('textLength', String(advance * width / bounds));
        node.setAttribute('lengthAdjust', 'spacingAndGlyphs');
      }
    });
  }
  var features = document.querySelector('.features');
  if (features) new MutationObserver(translateCarousel).observe(features, { childList: true, subtree: true });
  window.addEventListener('resize', fitDiagramLabels);
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(fitDiagramLabels);
  var LANGS = ['en','cs','es','de','fr','ja','kk','pl','ru','tr','zh','zh-cn','bn','hu','it'];
  var FLAGS = {
    en: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><g fill="#b22234"><rect width="24" height="1.85"/><rect y="3.7" width="24" height="1.85"/><rect y="7.4" width="24" height="1.85"/><rect y="11.1" width="24" height="1.85"/><rect y="14.8" width="24" height="1.85"/><rect y="18.5" width="24" height="1.85"/><rect y="22.2" width="24" height="1.8"/></g><rect width="11" height="12.95" fill="#3c3b6e"/></svg>',
    cs: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#d7141a"/><polygon points="0,0 12,12 0,24" fill="#11457e"/></svg>',
    es: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#c60b1e"/><rect y="6" width="24" height="12" fill="#ffc400"/></svg>',
    de: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#000"/><rect y="8" width="24" height="8" fill="#dd0000"/><rect y="16" width="24" height="8" fill="#ffce00"/></svg>',
    fr: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#0055a4"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ef4135"/></svg>',
    ja: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><circle cx="12" cy="12" r="7" fill="#bc002d"/></svg>',
    kk: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#00afca"/><circle cx="12" cy="12" r="4.5" fill="#f6d34a"/><g stroke="#f6d34a" stroke-width="1"><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1"/></g></svg>',
    pl: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#dc143c"/></svg>',
    ru: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#fff"/><rect y="8" width="24" height="8" fill="#0039a6"/><rect y="16" width="24" height="8" fill="#d52b1e"/></svg>',
    tr: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#e30a17"/><circle cx="9.5" cy="12" r="5" fill="#fff"/><circle cx="11" cy="12" r="4" fill="#e30a17"/><polygon points="15.5,9.4 16.12,11.15 17.97,11.2 16.5,12.32 17.03,14.1 15.5,13.05 13.97,14.1 14.5,12.32 13.03,11.2 14.88,11.15" fill="#fff"/></svg>',
    zh: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fe0000"/><rect width="12" height="12" fill="#000095"/><polygon points="6,3 7.2,6.6 11,6.6 7.9,8.8 9.1,12.4 6,10.2 2.9,12.4 4.1,8.8 1,6.6 4.8,6.6" fill="#fff"/></svg>',
    'zh-cn': '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#de2910"/><polygon points="4,3 4.9,5.6 7.6,5.6 5.4,7.3 6.2,9.9 4,8.3 1.8,9.9 2.6,7.3 0.4,5.6 3.1,5.6" fill="#ffde00"/></svg>',
    bn: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#006a4e"/><circle cx="10.5" cy="12" r="6" fill="#f42a41"/></svg>',
    hu: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#ce2939"/><rect y="8" width="24" height="8" fill="#fff"/><rect y="16" width="24" height="8" fill="#477050"/></svg>',
    it: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#009246"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ce2b37"/></svg>'
  };
  var langItems = [].slice.call(document.querySelectorAll('.lang-item'));
  var langToggle = document.getElementById('langToggle');
  var langMenu = document.getElementById('langMenu');

  function applyLang(lang) {
    if (LANGS.indexOf(lang) < 0) lang = 'en';
    currentLang = lang;
    root.setAttribute('lang', lang === 'zh' ? 'zh-Hant' : (lang === 'zh-cn' ? 'zh-Hans' : lang));
    var dict = (lang === 'en') ? EN : (I18N[lang] || {});
    document.querySelectorAll('[data-i18n]').forEach(function (n) {
      var k = n.getAttribute('data-i18n');
      n.innerHTML = (dict && dict[k] != null) ? dict[k] : EN[k];
    });
    translateAttributes();
    translateCarousel();
    fitDiagramLabels();
    langItems.forEach(function (b) { b.setAttribute('aria-pressed', b.dataset.lang === lang ? 'true' : 'false'); });
    if (langToggle) langToggle.innerHTML = FLAGS[lang] || FLAGS.en;
    try { localStorage.setItem('kscan-lang', lang); } catch (e) {}
    // For text that is not in the DOM as a data-i18n node - the screenshot captions, which live
    // in a script array and are used as alt/title/aria-label - so those follow the language too.
    document.dispatchEvent(new CustomEvent('kscan-languagechange', { detail: { lang: lang } }));
  }
  function closeLangMenu() { if (langMenu) { langMenu.hidden = true; langToggle.setAttribute('aria-expanded', 'false'); } }
  if (langToggle && langMenu) {
    langToggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var willOpen = langMenu.hidden;
      langMenu.hidden = !willOpen;
      langToggle.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
    });
    langItems.forEach(function (b) { b.addEventListener('click', function () { applyLang(b.dataset.lang); closeLangMenu(); }); });
    document.addEventListener('click', function (e) { if (!langMenu.hidden && !e.target.closest('.lang-switch')) closeLangMenu(); });
  }

  // ---- Easter egg: click the version number ----
  var verEgg = document.getElementById('verEgg');
  var eggToast = document.getElementById('eggToast');
  if (verEgg) verEgg.addEventListener('click', function () {
    for (var i = 0; i < 18; i++) {
      var d = document.createElement('span');
      d.className = 'drip';
      d.style.left = (Math.random() * 100) + 'vw';
      d.style.height = (18 + Math.random() * 64) + 'px';
      d.style.opacity = (0.6 + Math.random() * 0.4).toFixed(2);
      var dur = 1.1 + Math.random() * 1.6;
      d.style.animation = 'dripfall ' + dur + 's linear forwards';
      d.style.animationDelay = (Math.random() * 0.5) + 's';
      document.body.appendChild(d);
      (function (el) { setTimeout(function () { el.remove(); }, (dur + 0.8) * 1000); })(d);
    }
    if (eggToast) {
      eggToast.textContent = uiText('ui_egg');
      eggToast.classList.add('show');
      clearTimeout(verEgg._t);
      verEgg._t = setTimeout(function () { eggToast.classList.remove('show'); }, 2800);
    }
  });

  // ---- Init ----
  var savedTheme = 'hc', savedAccent = 'orange', savedLang = 'en';
  try {
    savedTheme  = localStorage.getItem('kscan-theme')  || savedTheme;
    savedAccent = localStorage.getItem('kscan-accent') || savedAccent;
    savedLang   = localStorage.getItem('kscan-lang')   || savedLang;
  } catch (e) {}
  curAccent = savedAccent;
  setTheme(savedTheme);
  applyLang(savedLang);
})();
