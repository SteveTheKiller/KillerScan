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

  carousel.className = 'feature-carousel';
  carousel.setAttribute('aria-roledescription', 'carousel');
  carousel.setAttribute('aria-label', 'Product features');
  controls.className = 'feature-carousel-controls';
  previous.className = 'feature-carousel-arrow';
  previous.type = 'button';
  previous.setAttribute('aria-label', 'Previous feature');
  previous.innerHTML = '&#8592;';
  count.className = 'feature-carousel-count';
  count.setAttribute('aria-hidden', 'true');
  next.className = 'feature-carousel-arrow';
  next.type = 'button';
  next.setAttribute('aria-label', 'Next feature');
  next.innerHTML = '&#8594;';
  rail.className = 'feature-carousel-rail';
  rail.setAttribute('role', 'tablist');
  rail.setAttribute('aria-label', 'Choose a feature');

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
  show(0, false);
})();

