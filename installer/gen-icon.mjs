// 荔枝+锤子 应用图标生成器：icon.svg -> 多尺寸 BMP 条目 .ico
// 依赖 sharp（复用 site/node_modules，无需额外安装）。
// 用法：cd printer && node installer/gen-icon.mjs
//   产出 Resource/app.ico（16/24/32/48/64/128/256）与 Resource/tray.ico（16/20/24/32/48）
//   以及临时预览 temp/icon-preview-*.png（生成后可人工核看）。
import { createRequire } from "node:module";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const require = createRequire(join(root, "site", "package.json")); // sharp 由官网构建依赖提供
const sharp = require("sharp");

const svg = readFileSync(join(here, "icon.svg"));

async function renderRgba(size) {
  const buf = await sharp(svg, { density: 72 * (size / 40) * (256 / 40) })
    .resize(size, size)
    .ensureAlpha()
    .raw()
    .toBuffer({ resolveWithObject: true });
  if (buf.info.width !== size || buf.info.height !== size)
    throw new Error(`render size mismatch: ${buf.info.width}x${buf.info.height}`);
  return buf.data; // RGBA
}

// 单尺寸 BMP（32bpp、底向上、BGRA、含全零 AND 掩码）——Windows 原生兼容格式
function bmpEntry(size, rgba) {
  const w = size, h = size;
  const andStride = (((w + 31) >> 5) << 2); // 每行按 4 字节对齐的掩码
  const andSize = andStride * h;
  const xorSize = w * h * 4;
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);        // biSize
  header.writeInt32LE(w, 4);         // biWidth
  header.writeInt32LE(h * 2, 8);      // biHeight（XOR + AND）
  header.writeUInt16LE(1, 12);        // biPlanes
  header.writeUInt16LE(32, 14);       // biBitCount
  header.writeUInt32LE(0, 16);        // biCompression = BI_RGB
  header.writeUInt32LE(xorSize + andSize, 20);
  const pixels = Buffer.alloc(xorSize);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const src = ((h - 1 - y) * w + x) * 4; // 翻转为底向上
      const dst = (y * w + x) * 4;
      pixels[dst] = rgba[src + 2];     // B
      pixels[dst + 1] = rgba[src + 1]; // G
      pixels[dst + 2] = rgba[src];     // R
      pixels[dst + 3] = rgba[src + 3]; // A
    }
  }
  return Buffer.concat([header, pixels, Buffer.alloc(andSize)]);
}

async function buildIco(sizes, outFile) {
  const entries = [];
  for (const size of sizes) entries.push({ size, data: bmpEntry(size, await renderRgba(size)) });
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2); // 类型 1 = icon
  header.writeUInt16LE(entries.length, 4);
  const dir = Buffer.alloc(entries.length * 16);
  let offset = 6 + dir.length;
  entries.forEach((e, i) => {
    const b = i * 16;
    dir.writeUInt8(e.size >= 256 ? 0 : e.size, b);
    dir.writeUInt8(e.size >= 256 ? 0 : e.size, b + 1);
    dir.writeUInt16LE(1, b + 4);       // planes
    dir.writeUInt16LE(32, b + 6);      // bpp
    dir.writeUInt32LE(e.data.length, b + 8);
    dir.writeUInt32LE(offset, b + 12);
    offset += e.data.length;
  });
  writeFileSync(outFile, Buffer.concat([header, dir, ...entries.map((e) => e.data)]));
  console.log(`written ${outFile} (${sizes.join("/")})`);
}

mkdirSync(join(root, "temp"), { recursive: true });
// 预览（人工核看设计）
const preview = await sharp(svg, { density: 460 }).resize(256, 256).png().toBuffer();
writeFileSync(join(root, "temp", "icon-preview-256.png"), preview);

await buildIco([16, 24, 32, 48, 64, 128, 256], join(root, "Resource", "app.ico"));
await buildIco([16, 20, 24, 32, 48], join(root, "Resource", "tray.ico"));
console.log("done");
