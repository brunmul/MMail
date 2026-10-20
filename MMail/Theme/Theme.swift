import SwiftUI

// MARK: - Color helpers

extension Color {
    init(hex: String) {
        let s = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        var v: UInt64 = 0
        Scanner(string: s).scanHexInt64(&v)
        let r, g, b, a: Double
        switch s.count {
        case 8:
            r = Double((v >> 24) & 0xFF) / 255
            g = Double((v >> 16) & 0xFF) / 255
            b = Double((v >> 8) & 0xFF) / 255
            a = Double(v & 0xFF) / 255
        default:
            r = Double((v >> 16) & 0xFF) / 255
            g = Double((v >> 8) & 0xFF) / 255
            b = Double(v & 0xFF) / 255
            a = 1
        }
        self.init(.sRGB, red: r, green: g, blue: b, opacity: a)
    }

    static func rgba(_ r: Double, _ g: Double, _ b: Double, _ a: Double) -> Color {
        Color(.sRGB, red: r / 255, green: g / 255, blue: b / 255, opacity: a)
    }
}

// MARK: - Palette (Arcos: warm paper and clay, light + dark token sets)

struct Palette {
    // Brand
    let brandBlue: Color
    let brandBlue600: Color
    let brandBlue700: Color
    let brandBlue100: Color
    let brandBlue50: Color
    let magenta: Color
    let magenta600: Color
    let magenta100: Color

    // Neutral text
    let fg1: Color
    let fg2: Color
    let fg3: Color
    let fg4: Color

    // Surfaces
    let bg1: Color
    let bg2: Color
    let bg3: Color
    let bg4: Color

    // Lines
    let border: Color
    let borderStrong: Color

    // Semantic
    let success: Color
    let success100: Color
    let warning: Color
    let warning100: Color
    let danger: Color
    let danger100: Color

    // For folder-active text in dark mode
    let activeFolderText: Color

    // Arcos surfaces
    let composeBg: Color      // pastel "Compose" button
    let composeFg: Color
    let searchBg: Color       // pill search field
    let readRow: Color        // tint behind already-read rows
    let star: Color

    let isDark: Bool

    // Arcos light tokens: warm paper, one touch of clay (the look of Claude).
    static let light = Palette(
        brandBlue: Color(hex: "C15F3C"),      // clay, deepened so it reads as text
        brandBlue600: Color(hex: "A9502F"),
        brandBlue700: Color(hex: "8E4226"),
        brandBlue100: Color(hex: "F3E4DA"),   // clay wash: selection
        brandBlue50: Color(hex: "F9F0EA"),
        magenta: Color(hex: "C4453A"),
        magenta600: Color(hex: "A83A30"),
        magenta100: Color(hex: "F8E5E2"),
        fg1: Color(hex: "262624"),
        fg2: Color(hex: "5E5D59"),
        fg3: Color(hex: "7D7C76"),
        fg4: Color(hex: "A9A79F"),
        bg1: Color(hex: "FFFFFF"),
        bg2: Color(hex: "F5F4ED"),            // surface: the paper of the bar
        bg3: Color(hex: "EFEEE6"),
        bg4: Color(hex: "E6E4DA"),
        border: .rgba(0, 0, 0, 0.08),
        borderStrong: .rgba(0, 0, 0, 0.18),
        success: Color(hex: "5C7A47"),
        success100: Color(hex: "E9EFE3"),
        warning: Color(hex: "C08A2E"),
        warning100: Color(hex: "F7EEDC"),
        danger: Color(hex: "C4453A"),
        danger100: Color(hex: "F8E5E2"),
        activeFolderText: Color(hex: "262624"),
        composeBg: Color(hex: "FFFFFF"),      // the white "current" pill
        composeFg: Color(hex: "262624"),
        searchBg: Color(hex: "FFFFFF"),
        readRow: Color(hex: "FAF9F5"),
        star: Color(hex: "D97757"),
        isDark: false
    )

    // Arcos dark tokens.
    static let dark = Palette(
        brandBlue: Color(hex: "E08A6A"),
        brandBlue600: Color(hex: "D97757"),
        brandBlue700: Color(hex: "C15F3C"),
        brandBlue100: .rgba(217, 119, 87, 0.22),
        brandBlue50: .rgba(217, 119, 87, 0.12),
        magenta: Color(hex: "E5847A"),
        magenta600: Color(hex: "D96C61"),
        magenta100: .rgba(229, 132, 122, 0.16),
        fg1: Color(hex: "E8E6E1"),
        fg2: Color(hex: "B3B1AA"),
        fg3: Color(hex: "8C8A84"),
        fg4: Color(hex: "6B6965"),
        bg1: Color(hex: "2A2928"),
        bg2: Color(hex: "1F1E1D"),
        bg3: Color(hex: "333230"),
        bg4: Color(hex: "3D3C39"),
        border: .rgba(255, 255, 255, 0.08),
        borderStrong: .rgba(255, 255, 255, 0.18),
        success: Color(hex: "A0B295"),
        success100: .rgba(160, 178, 149, 0.16),
        warning: Color(hex: "D9B66E"),
        warning100: .rgba(217, 182, 110, 0.16),
        danger: Color(hex: "E5847A"),
        danger100: .rgba(229, 132, 122, 0.16),
        activeFolderText: Color(hex: "E8E6E1"),
        composeBg: .rgba(255, 255, 255, 0.12),
        composeFg: Color(hex: "E8E6E1"),
        searchBg: Color(hex: "1F1E1D"),
        readRow: Color(hex: "262524"),
        star: Color(hex: "D97757"),
        isDark: true
    )
}

private struct PaletteKey: EnvironmentKey {
    static let defaultValue: Palette = .light
}

extension EnvironmentValues {
    var palette: Palette {
        get { self[PaletteKey.self] }
        set { self[PaletteKey.self] = newValue }
    }
}

// MARK: - Typography scale (native SF Pro; design used Plus Jakarta Sans)

enum Typo {
    static func display() -> Font { .system(size: 64, weight: .heavy) }
    static func h1() -> Font { .system(size: 48, weight: .heavy) }
    static func h2() -> Font { .system(size: 36, weight: .bold) }
    static func h3() -> Font { .system(size: 30, weight: .bold) }
    static func mono(_ size: CGFloat) -> Font { .system(size: size, design: .monospaced) }
    /// The serif (New York) used for the greeting and message subjects.
    static func serif(_ size: CGFloat) -> Font { .system(size: size, weight: .regular, design: .serif) }
}

// MARK: - Arcos shapes

enum Arcos {
    static let clay = Color(hex: "D97757")
    static let badge = Color(hex: "FF3B30")
    /// Soft shadow under the white "current" pill.
    static let currentShadow = Color.black.opacity(0.10)

    static func hoverFill(_ p: Palette) -> Color { p.isDark ? .white.opacity(0.06) : .black.opacity(0.06) }
    static func chipFill(_ p: Palette) -> Color { p.isDark ? .white.opacity(0.07) : .black.opacity(0.07) }
    static func current(_ p: Palette) -> Color { p.isDark ? .white.opacity(0.18) : .white.opacity(0.78) }
    static func outline(_ p: Palette) -> Color { p.isDark ? .white.opacity(0.18) : .black.opacity(0.18) }
    static func wave(_ p: Palette) -> Color { p.isDark ? .white.opacity(0.16) : .black.opacity(0.16) }
}

/// Arcos never draws straight dividers: a thin wavy line separates groups.
struct WaveSeparator: View {
    @Environment(\.palette) private var p

    var body: some View {
        Canvas { ctx, size in
            var path = Path()
            let mid = size.height / 2
            let step: CGFloat = 6
            path.move(to: CGPoint(x: 1, y: mid))
            var x: CGFloat = 1
            var up = true
            while x < size.width - 1 {
                let next = min(x + step, size.width - 1)
                path.addQuadCurve(to: CGPoint(x: next, y: mid),
                                  control: CGPoint(x: (x + next) / 2, y: up ? 0 : size.height))
                up.toggle()
                x = next
            }
            ctx.stroke(path, with: .color(Arcos.wave(p)), style: StrokeStyle(lineWidth: 1, lineCap: .round))
        }
        .frame(height: 5)
        .padding(.vertical, 6)
    }
}
