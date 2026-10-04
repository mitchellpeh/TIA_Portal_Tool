"""Writes TiaPortalTool/Themes/Palette.*.xaml from one table so the palettes always have the same keys.

Usage: python tools/make-palettes.py   (then rebuild the app). Edit colours here, not in the .xaml files."""
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'TiaPortalTool', 'Themes')
NAMES = ['Modern', 'Win95']
NOTES = {
    'Modern': 'Modern: dark slate and blue.',
    'Win95': 'Windows 95: grey 3D controls on a teal desktop.',
}

# key: (Modern, Win95)
P = [
    ('# Window background and text drawn straight on it (headers, version line)', None),
    ('Window.Background',      '#0f172a', '#008080'),
    ('Window.Text',            '#f8fafc', '#ffffff'),
    ('Window.TextMuted',       '#64748b', '#e0e0e0'),
    ('# Cards and inner boxes', None),
    ('Card.Background',        '#111827', '#c0c0c0'),
    ('Card.Border',            '#334155', '#808080'),
    ('Section.Background',     '#0b1220', '#c0c0c0'),
    ('Section.Border',         '#1e293b', '#808080'),
    ('# Text on cards', None),
    ('Text.Strong',            '#f8fafc', '#000000'),
    ('Text',                   '#e2e8f0', '#000000'),
    ('Text.Body',              '#cbd5e1', '#000000'),
    ('Text.Muted',             '#94a3b8', '#404040'),
    ('Text.Faint',             '#64748b', '#606060'),
    ('Notice',                 '#f59e0b', '#800000'),
    ('Hint.Success',           '#4ade80', '#006000'),
    ('Hint.Warning',           '#fbbf24', '#806000'),
    ('Hint.Error',             '#f87171', '#c00000'),
    ('# Text boxes', None),
    ('Input.Background',       '#0f172a', '#ffffff'),
    ('Input.Foreground',       '#e2e8f0', '#000000'),
    ('Input.Border',           '#334155', '#808080'),
    ('# Buttons. Accent = the primary (default) button.', None),
    ('Accent.Background',      '#2563eb', '#c0c0c0'),
    ('Accent.Foreground',      '#ffffff', '#000000'),
    ('Accent.Border',          '#2563eb', '#000000'),
    ('Accent.Hover',           '#3b82f6', '#000080'),
    ('Focus',                  '#93c5fd', '#000000'),
    ('Button.Background',      '#1f2937', '#c0c0c0'),
    ('Button.Foreground',      '#e2e8f0', '#000000'),
    ('Button.Border',          '#475569', '#c0c0c0'),
    ('# 3D bevel edges (Windows 95 controls only)', None),
    ('Bevel.Light',            '#475569', '#ffffff'),
    ('Bevel.Shadow',           '#1e293b', '#808080'),
    ('Bevel.Dark',             '#020617', '#000000'),
    ('# Launcher option cards', None),
    ('Launch.HoverBackground', '#172033', '#d8d8d8'),
    ('# Scroll bars', None),
    ('Scroll.Thumb',           '#334155', '#808080'),
    ('Scroll.ThumbHover',      '#475569', '#606060'),
    ('Scroll.ThumbDrag',       '#64748b', '#404040'),
    ('# Version and result badges', None),
    ('Badge.Pre.Background',   '#78350f', '#000080'),
    ('Badge.Pre.Foreground',   '#fde68a', '#ffffff'),
    ('Badge.Done.Background',  '#14532d', '#008000'),
    ('Badge.Done.Foreground',  '#bbf7d0', '#ffffff'),
    ('Badge.Fail.Background',  '#7f1d1d', '#800000'),
    ('Badge.Fail.Foreground',  '#fecaca', '#ffffff'),
    ('Badge.Neutral.Background', '#334155', '#808080'),
    ('Badge.Neutral.Foreground', '#e2e8f0', '#ffffff'),
    ('# Output pane', None),
    ('Log.Background',         '#020617', '#ffffff'),
    ('Log.Border',             '#1e293b', '#808080'),
    ('Log.Text',               '#cbd5e1', '#000000'),
    ('Log.Time',               '#64748b', '#808080'),
    ('Log.Success',            '#4ade80', '#008000'),
    ('Log.Warning',            '#fbbf24', '#806000'),
    ('Log.Error',              '#f87171', '#c00000'),
    ('# Status bar progress and the waiting-for-you pulse', None),
    ('Progress.Background',    '#1e293b', '#ffffff'),
    ('Progress.Foreground',    '#3b82f6', '#000080'),
    ('Pulse.Border',           '#fbbf24', '#ffff00'),
    ('Pulse.Background',       '#3a2e0c', '#ffffc0'),
]

os.makedirs(OUT, exist_ok=True)
for i, name in enumerate(NAMES):
    lines = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
             f'    <!-- {NOTES[name]} Generated with the other palette from one table; keep their keys identical. -->']
    for row in P:
        if row[1] is None:
            lines.append(f'    <!-- {row[0][2:]} -->')
            continue
        lines.append(f'    <SolidColorBrush x:Key="{row[0]}" Color="{row[1 + i]}"/>')
    lines.append('</ResourceDictionary>')
    with open(os.path.join(OUT, f'Palette.{name}.xaml'), 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('\n'.join(lines) + '\n')
print('wrote', len(NAMES), 'palettes,', sum(1 for r in P if r[1]), 'brushes each')
