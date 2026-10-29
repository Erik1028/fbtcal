"""fbtcal's icons: one SVG design, rendered by Edge (headless) and packed with Pillow.

The design is just a SlimeVR body tracker, after the real one: a translucent purple case, domed on
top with three little ridges, the board showing through (the metal ESP module and its antenna), the
USB-C port at the bottom, on a black hook-and-loop strap. No background. The case colour shows the
tray state: purple = watching, orange = ideal pose on / capturing, grey = waiting for SteamVR.
Small sizes (32 px and less: tray, taskbar) leave out the board details and the strap texture.

    python art/make_icons.py          -> art/out/*.png, tray/res/*.ico + *.png, site/favicon.svg
"""
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "art" / "out"
RES = ROOT / "tray" / "res"
EDGE = Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")

# the case: light -> mid -> dark (outline), the board seen through it
PALETTES = {
    "app": ("#D9A6FF", "#A743E8", "#6B1DA6", "#7E2BB8"),       # SlimeVR purple
    "watching": ("#D9A6FF", "#A743E8", "#6B1DA6", "#7E2BB8"),
    "active": ("#FFD39A", "#F08A2C", "#A9520F", "#C2661A"),     # orange: ideal pose on / capturing
    "waiting": ("#D7DCE0", "#959EA6", "#5A6168", "#737C84"),    # grey: no SteamVR
}

CASE = "M29 47 C29 31 43 25 60 25 C77 25 91 31 91 47 L91 83 Q91 94 80 94 L40 94 Q29 94 29 83 Z"


def svg(palette, simple=False):
    light, mid, dark, board = PALETTES[palette]
    strap_texture = "" if simple else """
    <rect x="3" y="47" width="114" height="36" rx="7" fill="url(#velcro)"/>"""
    details = "" if simple else f"""
  <rect x="37" y="37" width="46" height="46" rx="6" fill="{board}" fill-opacity=".55"/>
  <path d="M62 44 h4 v-4 h4 v4 h4 v-4 h4" stroke="#F3D9FF" stroke-opacity=".8" stroke-width="1.6" fill="none"/>
  <rect x="61" y="50" width="17" height="17" rx="2" fill="#F1DEFA" fill-opacity=".85"/>
  <rect x="64" y="53" width="11" height="11" rx="1.5" fill="none" stroke="{board}" stroke-opacity=".5" stroke-width="1"/>
  <rect x="44" y="54" width="8" height="8" rx="1.2" fill="#2A1438" fill-opacity=".8"/>
  <g fill="#F3D9FF" fill-opacity=".7"><circle cx="41" cy="45" r="1.2"/><circle cx="41" cy="50" r="1.2"/>
    <circle cx="41" cy="55" r="1.2"/><circle cx="41" cy="60" r="1.2"/><circle cx="41" cy="65" r="1.2"/></g>
  <rect x="45" y="71" width="9" height="5" rx="1" fill="#2A1438" fill-opacity=".7"/>
  <path d="M48 26.5 v6 M60 25.2 v6.5 M72 26.5 v6" stroke="{dark}" stroke-width="2.4" stroke-linecap="round"/>
  <rect x="53" y="86" width="14" height="5" rx="2.5" fill="#2A1438" fill-opacity=".75"/>
  <ellipse cx="45" cy="38" rx="11" ry="6" fill="#fff" fill-opacity=".38" transform="rotate(-25 45 38)"/>"""
    if simple:
        details = f"""
  <rect x="38" y="38" width="44" height="44" rx="6" fill="{board}" fill-opacity=".5"/>
  <rect x="58" y="48" width="18" height="18" rx="2" fill="#fff" fill-opacity=".75"/>"""
    zoom = "translate(60 61) scale(1.22) translate(-60 -60)" if simple else ""  # small: the case fills the icon
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120">
  <defs>
    <linearGradient id="case" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="{light}"/><stop offset=".5" stop-color="{mid}"/><stop offset="1" stop-color="{dark}"/>
    </linearGradient>
    <linearGradient id="strap" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#2B2B2E"/><stop offset=".5" stop-color="#18181A"/><stop offset="1" stop-color="#0E0E10"/>
    </linearGradient>
    <pattern id="velcro" width="3" height="3" patternUnits="userSpaceOnUse">
      <circle cx="1.5" cy="1.5" r=".7" fill="#fff" fill-opacity=".09"/>
    </pattern>
  </defs>
  <g transform="{zoom}">
  <rect x="3" y="47" width="114" height="36" rx="7" fill="url(#strap)"/>{strap_texture}
  <path d="{CASE}" transform="translate(0 3)" fill="#000" fill-opacity=".28"/>
  <path d="{CASE}" fill="url(#case)" fill-opacity=".93" stroke="{dark}" stroke-width="2.4"/>{details}
  </g>
</svg>
"""


def render(svg_text, px, work):
    """SVG -> RGBA image of px x px, through Edge's renderer at 4x and a Lanczos downscale."""
    big = px * 4 if px < 256 else px
    src = work / f"icon_{abs(hash(svg_text))}_{big}.svg"
    src.write_text(svg_text, encoding="utf-8")
    page = src.with_suffix(".html")
    page.write_text(f'<html><body style="margin:0;background:transparent">'
                    f'<img src="{src.name}" width="{big}" height="{big}" style="display:block"></body></html>',
                    encoding="utf-8")
    shot = src.with_suffix(".png")
    subprocess.run([str(EDGE), "--headless=new", "--disable-gpu", "--hide-scrollbars",
                    "--default-background-color=00000000", f"--window-size={big},{big}",
                    f"--screenshot={shot}", page.as_uri()], check=True, capture_output=True, timeout=60)
    img = Image.open(shot).convert("RGBA").crop((0, 0, big, big))
    return img.resize((px, px), Image.LANCZOS) if big != px else img


def icon_set(palette, sizes, work):
    return [render(svg(palette, simple=s <= 32), s, work) for s in sizes]


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    RES.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="fbtcal-icons-"))
    try:
        app_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
        imgs = icon_set("app", app_sizes, work)
        imgs[-1].save(RES / "fbtcal.ico", sizes=[(s, s) for s in app_sizes], append_images=imgs[:-1])
        for s, im in zip(app_sizes, imgs):
            im.save(OUT / f"fbtcal-{s}.png")
        render(svg("app"), 512, work).save(OUT / "fbtcal-512.png")
        render(svg("app"), 256, work).save(RES / "thumb.png")  # SteamVR dashboard tab
        tray_sizes = [16, 20, 24, 32, 40, 48, 64]
        for state in ("watching", "active", "waiting"):
            t = icon_set(state, tray_sizes, work)
            t[-1].save(RES / f"tray-{state}.ico", sizes=[(s, s) for s in tray_sizes], append_images=t[:-1])
            render(svg(state), 128, work).save(RES / f"head-{state}.png")  # the panel's header
        (OUT / "fbtcal.svg").write_text(svg("app"), encoding="utf-8")
        site = ROOT / "site"
        if site.is_dir():
            shutil.copy(OUT / "fbtcal.svg", site / "favicon.svg")
        # contact sheet to look at
        sheet = Image.new("RGBA", (900, 300), (18, 22, 26, 255))
        x = 10
        for s, im in zip(app_sizes, imgs):
            sheet.alpha_composite(im, (x, 150 - s // 2))
            x += s + 14
        for i, state in enumerate(("watching", "active", "waiting")):
            for j, s in enumerate((16, 32, 64)):
                im = render(svg(state, simple=s <= 32), s, work)
                sheet.alpha_composite(im, (600 + j * 90, 20 + i * 90))
        sheet.save(OUT / "sheet.png")
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print("icons ->", RES, "and", OUT)


if __name__ == "__main__":
    sys.exit(main())
