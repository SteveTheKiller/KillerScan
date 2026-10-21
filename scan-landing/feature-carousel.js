(function () {
  var grid = document.querySelector('.features .feature-grid');
  if (!grid || grid.closest('.feature-carousel')) return;
  document.documentElement.classList.add('js');

  var cards = Array.prototype.slice.call(grid.querySelectorAll('.feature-card'));
  if (!cards.length) return;

  var carousel = document.createElement('div');
  var controls = document.createElement('div');
  var previous = document.createElement('button');
  var count = document.createElement('span');
  var next = document.createElement('button');
  var rail = document.createElement('div');
  var tabs = [];
  var index = 0;
  var labels = {
    "sl": ["Možnosti programa", "Prejšnja možnost", "Naslednja možnost", "Izberi možnost", "vrtiljak", "diapozitiv"],
    "en": ["Product features", "Previous feature", "Next feature", "Choose a feature"],
    "bn": ["পণ্যের বৈশিষ্ট্য", "আগের বৈশিষ্ট্য", "পরের বৈশিষ্ট্য", "একটি বৈশিষ্ট্য বেছে নিন"],
    "cs": ["Funkce produktu", "Předchozí funkce", "Další funkce", "Vybrat funkci"],
    "de": ["Produktfunktionen", "Vorherige Funktion", "Nächste Funktion", "Funktion auswählen"],
    "es": ["Funciones del producto", "Función anterior", "Función siguiente", "Elegir una función"],
    "fr": ["Fonctionnalités du produit", "Fonctionnalité précédente", "Fonctionnalité suivante", "Choisir une fonctionnalité"],
    "hu": ["A termék funkciói", "Előző funkció", "Következő funkció", "Funkció kiválasztása"],
    "it": ["Funzionalità del prodotto", "Funzionalità precedente", "Funzionalità successiva", "Scegli una funzionalità"],
    "ja": ["製品の機能", "前の機能", "次の機能", "機能を選択"],
    "kk": ["Өнім мүмкіндіктері", "Алдыңғы мүмкіндік", "Келесі мүмкіндік", "Мүмкіндікті таңдау"],
    "nb": ["Produktfunksjoner", "Forrige funksjon", "Neste funksjon", "Velg en funksjon"],
    "pl": ["Funkcje produktu", "Poprzednia funkcja", "Następna funkcja", "Wybierz funkcję"],
    "pt": ["Recursos do produto", "Recurso anterior", "Próximo recurso", "Escolher um recurso"],
    "ru": ["Возможности продукта", "Предыдущая возможность", "Следующая возможность", "Выбрать возможность"],
    "tr": ["Ürün özellikleri", "Önceki özellik", "Sonraki özellik", "Bir özellik seçin"],
    "uk": ["Можливості продукту", "Попередня можливість", "Наступна можливість", "Вибрати можливість"],
    "vi": ["Tính năng sản phẩm", "Tính năng trước", "Tính năng tiếp theo", "Chọn một tính năng"],
    "zh-cn": ["产品功能", "上一个功能", "下一个功能", "选择功能"],
    "zh": ["產品功能", "上一個功能", "下一個功能", "選擇功能"]
  };

  carousel.className = 'feature-carousel';
  carousel.setAttribute('aria-roledescription', 'carousel');
  controls.className = 'feature-carousel-controls';
  previous.className = 'feature-carousel-arrow';
  previous.type = 'button';
  previous.innerHTML = '&#8592;';
  count.className = 'feature-carousel-count';
  count.setAttribute('aria-hidden', 'true');
  next.className = 'feature-carousel-arrow';
  next.type = 'button';
  next.innerHTML = '&#8594;';
  rail.className = 'feature-carousel-rail';
  rail.setAttribute('role', 'tablist');

  grid.parentNode.insertBefore(carousel, grid);
  controls.appendChild(previous);
  controls.appendChild(count);
  controls.appendChild(next);
  carousel.appendChild(controls);
  carousel.appendChild(grid);
  carousel.appendChild(rail);
  grid.setAttribute('aria-live', 'polite');

  cards.forEach(function (card, cardIndex) {
    var heading = card.querySelector('h3');
    var label = heading ? heading.textContent.trim() : 'Feature ' + (cardIndex + 1);
    var tab = document.createElement('button');
    card.id = 'feature-slide-' + cardIndex;
    card.setAttribute('role', 'tabpanel');
    card.setAttribute('aria-roledescription', 'slide');
    card.setAttribute('aria-labelledby', 'feature-tab-' + cardIndex);
    tab.className = 'feature-carousel-tab';
    tab.type = 'button';
    tab.id = 'feature-tab-' + cardIndex;
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-controls', card.id);
    tab.textContent = label;
    tab.title = label;
    tab.setAttribute('aria-label', label);
    tab.addEventListener('click', function () { show(cardIndex, true); });
    rail.appendChild(tab);
    tabs.push(tab);
  });

  function labelSet() {
    var lang = (document.documentElement.getAttribute('lang') || 'en').toLowerCase();
    if (lang.indexOf('zh') === 0) lang = /hant|tw|hk|^zh$/.test(lang) ? 'zh' : 'zh-cn';
    else lang = lang.split('-')[0];
    return labels[lang] || labels.en;
  }

  function localizeControls() {
    var text = labelSet();
    carousel.setAttribute('aria-label', text[0]);
    carousel.setAttribute('aria-roledescription', text[4] || 'carousel');
    cards.forEach(function (card) { card.setAttribute('aria-roledescription', text[5] || 'slide'); });
    previous.setAttribute('aria-label', text[1]);
    next.setAttribute('aria-label', text[2]);
    rail.setAttribute('aria-label', text[3]);
  }

  function refreshLabels() {
    cards.forEach(function (card, cardIndex) {
      var heading = card.querySelector('h3');
      if (!heading) return;
      var label = heading.textContent.trim();
      tabs[cardIndex].textContent = label;
      tabs[cardIndex].title = label;
      tabs[cardIndex].setAttribute('aria-label', label);
    });
  }

  function show(nextIndex, focusTab) {
    index = (nextIndex + cards.length) % cards.length;
    cards.forEach(function (card, cardIndex) {
      var active = cardIndex === index;
      card.classList.toggle('is-active', active);
      card.hidden = !active;
      tabs[cardIndex].setAttribute('aria-selected', active ? 'true' : 'false');
      tabs[cardIndex].tabIndex = active ? 0 : -1;
    });
    count.textContent = String(index + 1).padStart(2, '0') + ' / ' + String(cards.length).padStart(2, '0');
    if (focusTab) tabs[index].focus({ preventScroll: true });
  }

  previous.addEventListener('click', function () { show(index - 1, false); });
  next.addEventListener('click', function () { show(index + 1, false); });
  carousel.addEventListener('keydown', function (event) {
    if (event.key === 'ArrowLeft') { show(index - 1, true); event.preventDefault(); }
    else if (event.key === 'ArrowRight') { show(index + 1, true); event.preventDefault(); }
    else if (event.key === 'Home') { show(0, true); event.preventDefault(); }
    else if (event.key === 'End') { show(cards.length - 1, true); event.preventDefault(); }
  });

  new MutationObserver(refreshLabels).observe(grid, {
    subtree: true,
    characterData: true,
    childList: true
  });
  new MutationObserver(localizeControls).observe(document.documentElement, {
    attributes: true,
    attributeFilter: ['lang']
  });
  localizeControls();
  show(0, false);
})();

