const particleCount = 2600;
const duration = 1400;
const entryYellow = "#ffdb38";
const logoYellow = [253, 201, 23];
type Point = { x: number; y: number };

export function consumePortalEntry(): boolean {
  const url = new URL(location.href);
  if (url.searchParams.get("entry") === "fyr-portal") {
    url.searchParams.delete("entry");
    // Keep the portal's language and other navigation state intact.
    history.replaceState(history.state, "", url);
  }
  return document.documentElement.dataset.docparseEntry === "pending";
}

function sampleLogo(image: HTMLImageElement): Point[] {
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 256;
  const context = canvas.getContext("2d");
  if (!context) return [];
  context.drawImage(image, 0, 0, 256, 256);
  const pixels = context.getImageData(0, 0, 256, 256).data;
  const points: Point[] = [];
  for (let y = 0; y < 256; y += 2) {
    for (let x = 0; x < 256; x += 2) {
      const index = (y * 256 + x) * 4;
      if (pixels[index + 3] > 100 && pixels[index] > 150)
        points.push({ x: x / 256, y: y / 256 });
    }
  }
  // Spread neighboring particles across the whole silhouette, deterministically.
  return Array.from({ length: particleCount }, (_, index) => {
    const hash = Math.sin((index + 1) * 12.9898) * 43758.5453;
    return points[Math.floor((hash - Math.floor(hash)) * points.length)];
  }).filter(Boolean);
}

export function playPortalEntry(): () => void {
  const root = document.documentElement;
  const motion = matchMedia("(prefers-reduced-motion: reduce)");
  const largeLogo = document.querySelector<HTMLImageElement>(".brand-art img");
  const headerLogo =
    document.querySelector<HTMLImageElement>(".product-brand img");
  const target = largeLogo?.getBoundingClientRect().width
    ? largeLogo
    : headerLogo;
  if (motion.matches || !target || !target.getBoundingClientRect().width) {
    delete root.dataset.docparseEntry;
    return () => {};
  }

  const canvas = document.createElement("canvas");
  canvas.className = "portal-entry";
  canvas.setAttribute("aria-hidden", "true");
  const context = canvas.getContext("2d");
  if (!context) {
    delete root.dataset.docparseEntry;
    return () => {};
  }
  const width = innerWidth;
  const height = innerHeight;
  const ratio = Math.min(devicePixelRatio || 1, 2);
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  context.scale(ratio, ratio);
  context.fillStyle = entryYellow;
  context.fillRect(0, 0, width, height);
  document.body.append(canvas);
  root.dataset.docparseEntry = "playing";
  const previousVisibility = target.style.visibility;
  target.style.visibility = "hidden";
  let frame = 0;
  let finished = false;
  const image = new Image();
  let timeout = 0;

  function finish() {
    if (finished) return;
    finished = true;
    cancelAnimationFrame(frame);
    clearTimeout(timeout);
    canvas.remove();
    target!.style.visibility = previousVisibility;
    delete root.dataset.docparseEntry;
    window.removeEventListener("resize", finish);
    window.removeEventListener("pagehide", finish);
    window.removeEventListener("pointerdown", finish, true);
    window.removeEventListener("keydown", finish, true);
    document.removeEventListener("visibilitychange", onVisibilityChange);
    motion.removeEventListener("change", onMotionChange);
    image.onload = image.onerror = null;
  }
  function onVisibilityChange() {
    if (document.hidden) finish();
  }
  function onMotionChange() {
    if (motion.matches) finish();
  }

  image.onload = () => {
    if (finished) return;
    let points: Point[];
    try {
      points = sampleLogo(image);
    } catch {
      finish();
      return;
    }
    if (points.length !== particleCount) {
      finish();
      return;
    }
    const box = target.getBoundingClientRect();
    const size = Math.min(box.width, box.height);
    const offsetX = box.left + (box.width - size) / 2;
    const offsetY = box.top + (box.height - size) / 2;
    const started = performance.now();
    const draw = (now: number) => {
      if (finished) return;
      const progress = Math.min(1, (now - started) / duration);
      const gather = Math.min(1, Math.max(0, (progress - 0.04) / 0.66));
      const eased = gather * gather * (3 - 2 * gather);
      const backdrop = Math.max(0, 1 - progress / 0.45);
      const opacity = 1 - Math.max(0, (progress - 0.88) / 0.12);
      context.clearRect(0, 0, width, height);
      context.globalAlpha = backdrop;
      context.fillStyle = entryYellow;
      context.fillRect(0, 0, width, height);
      context.globalAlpha = opacity;
      context.fillStyle = `rgb(${Math.round(255 + (logoYellow[0] - 255) * eased)} ${Math.round(219 + (logoYellow[1] - 219) * eased)} ${Math.round(56 + (logoYellow[2] - 56) * eased)})`;
      context.beginPath();
      for (let i = 0; i < particleCount; i++) {
        const startX = ((i * 0.61803398875) % 1) * width;
        const startY = ((i * 0.754877666) % 1) * height;
        const wave = Math.sin(Math.PI * gather);
        const x =
          startX * (1 - eased) +
          (offsetX + points[i].x * size) * eased +
          Math.sin(i * 2.39996) * wave * 20;
        const y = startY * (1 - eased) + (offsetY + points[i].y * size) * eased;
        const radius = 19.6 * (1 - eased) + Math.max(1, size / 170) * eased;
        context.moveTo(x, y - radius);
        context.lineTo(x + radius * 0.86, y + radius * 0.55);
        context.lineTo(x - radius * 0.86, y + radius * 0.55);
        context.closePath();
      }
      context.fill();
      context.globalAlpha = 1;
      // Crossfade onto the original animated SVG without moving the layout.
      if (progress >= 0.88) target.style.visibility = previousVisibility;
      if (progress < 1) frame = requestAnimationFrame(draw);
      else finish();
    };
    frame = requestAnimationFrame(draw);
  };
  image.onerror = finish;
  window.addEventListener("resize", finish);
  window.addEventListener("pagehide", finish);
  window.addEventListener("pointerdown", finish, true);
  window.addEventListener("keydown", finish, true);
  document.addEventListener("visibilitychange", onVisibilityChange);
  motion.addEventListener("change", onMotionChange);
  // Never leave an overlay behind if the logo fails or the tab stops rendering.
  timeout = window.setTimeout(finish, 2400);
  image.src = target.currentSrc || target.src;
  return finish;
}
