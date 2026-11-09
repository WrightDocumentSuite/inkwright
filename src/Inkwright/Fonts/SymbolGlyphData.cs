// Source: https://www.unicode.org/Public/MAPPINGS/VENDORS/ADOBE/symbol.txt
//   Copyright (c) 1991-2011 Unicode, Inc. All Rights reserved.
//
//   This file is provided as-is by Unicode, Inc. (The Unicode Consortium). No
//   claims are made as to fitness for any particular purpose. No warranties of
//   any kind are expressed or implied. The recipient agrees to determine
//   applicability of information provided. If this file has been provided on
//   magnetic media by Unicode, Inc., the sole remedy for any claim will be
//   exchange of defective media within 90 days of receipt.
//
//   Unicode, Inc. hereby grants the right to freely use the information
//   supplied in this file in the creation of products supporting the
//   Unicode Standard, and to make copies of this file in any form for
//   internal or external distribution as long as this notice remains
//   attached.
//

namespace Inkwright.Fonts;

internal static class SymbolGlyphData
{
    // One glyph per line: PostScript name, byte code (hex), Unicode (hex).
    // For aliases, keep the first Unicode entry. FontEncoding preserves existing Latin mappings.
    internal const string Table =
        "space 20 0020\n" +
        "exclam 21 0021\n" +
        "universal 22 2200\n" +
        "numbersign 23 0023\n" +
        "existential 24 2203\n" +
        "percent 25 0025\n" +
        "ampersand 26 0026\n" +
        "suchthat 27 220B\n" +
        "parenleft 28 0028\n" +
        "parenright 29 0029\n" +
        "asteriskmath 2A 2217\n" +
        "plus 2B 002B\n" +
        "comma 2C 002C\n" +
        "minus 2D 2212\n" +
        "period 2E 002E\n" +
        "slash 2F 002F\n" +
        "zero 30 0030\n" +
        "one 31 0031\n" +
        "two 32 0032\n" +
        "three 33 0033\n" +
        "four 34 0034\n" +
        "five 35 0035\n" +
        "six 36 0036\n" +
        "seven 37 0037\n" +
        "eight 38 0038\n" +
        "nine 39 0039\n" +
        "colon 3A 003A\n" +
        "semicolon 3B 003B\n" +
        "less 3C 003C\n" +
        "equal 3D 003D\n" +
        "greater 3E 003E\n" +
        "question 3F 003F\n" +
        "congruent 40 2245\n" +
        "Alpha 41 0391\n" +
        "Beta 42 0392\n" +
        "Chi 43 03A7\n" +
        "Delta 44 0394\n" +
        "Epsilon 45 0395\n" +
        "Phi 46 03A6\n" +
        "Gamma 47 0393\n" +
        "Eta 48 0397\n" +
        "Iota 49 0399\n" +
        "theta1 4A 03D1\n" +
        "Kappa 4B 039A\n" +
        "Lambda 4C 039B\n" +
        "Mu 4D 039C\n" +
        "Nu 4E 039D\n" +
        "Omicron 4F 039F\n" +
        "Pi 50 03A0\n" +
        "Theta 51 0398\n" +
        "Rho 52 03A1\n" +
        "Sigma 53 03A3\n" +
        "Tau 54 03A4\n" +
        "Upsilon 55 03A5\n" +
        "sigma1 56 03C2\n" +
        "Omega 57 03A9\n" +
        "Xi 58 039E\n" +
        "Psi 59 03A8\n" +
        "Zeta 5A 0396\n" +
        "bracketleft 5B 005B\n" +
        "therefore 5C 2234\n" +
        "bracketright 5D 005D\n" +
        "perpendicular 5E 22A5\n" +
        "underscore 5F 005F\n" +
        "radicalex 60 F8E5\n" +
        "alpha 61 03B1\n" +
        "beta 62 03B2\n" +
        "chi 63 03C7\n" +
        "delta 64 03B4\n" +
        "epsilon 65 03B5\n" +
        "phi 66 03C6\n" +
        "gamma 67 03B3\n" +
        "eta 68 03B7\n" +
        "iota 69 03B9\n" +
        "phi1 6A 03D5\n" +
        "kappa 6B 03BA\n" +
        "lambda 6C 03BB\n" +
        "mu 6D 00B5\n" +
        "nu 6E 03BD\n" +
        "omicron 6F 03BF\n" +
        "pi 70 03C0\n" +
        "theta 71 03B8\n" +
        "rho 72 03C1\n" +
        "sigma 73 03C3\n" +
        "tau 74 03C4\n" +
        "upsilon 75 03C5\n" +
        "omega1 76 03D6\n" +
        "omega 77 03C9\n" +
        "xi 78 03BE\n" +
        "psi 79 03C8\n" +
        "zeta 7A 03B6\n" +
        "braceleft 7B 007B\n" +
        "bar 7C 007C\n" +
        "braceright 7D 007D\n" +
        "similar 7E 223C\n" +
        "Euro A0 20AC\n" +
        "Upsilon1 A1 03D2\n" +
        "minute A2 2032\n" +
        "lessequal A3 2264\n" +
        "fraction A4 2044\n" +
        "infinity A5 221E\n" +
        "florin A6 0192\n" +
        "club A7 2663\n" +
        "diamond A8 2666\n" +
        "heart A9 2665\n" +
        "spade AA 2660\n" +
        "arrowboth AB 2194\n" +
        "arrowleft AC 2190\n" +
        "arrowup AD 2191\n" +
        "arrowright AE 2192\n" +
        "arrowdown AF 2193\n" +
        "degree B0 00B0\n" +
        "plusminus B1 00B1\n" +
        "second B2 2033\n" +
        "greaterequal B3 2265\n" +
        "multiply B4 00D7\n" +
        "proportional B5 221D\n" +
        "partialdiff B6 2202\n" +
        "bullet B7 2022\n" +
        "divide B8 00F7\n" +
        "notequal B9 2260\n" +
        "equivalence BA 2261\n" +
        "approxequal BB 2248\n" +
        "ellipsis BC 2026\n" +
        "arrowvertex BD F8E6\n" +
        "arrowhorizex BE F8E7\n" +
        "carriagereturn BF 21B5\n" +
        "aleph C0 2135\n" +
        "Ifraktur C1 2111\n" +
        "Rfraktur C2 211C\n" +
        "weierstrass C3 2118\n" +
        "circlemultiply C4 2297\n" +
        "circleplus C5 2295\n" +
        "emptyset C6 2205\n" +
        "intersection C7 2229\n" +
        "union C8 222A\n" +
        "propersuperset C9 2283\n" +
        "reflexsuperset CA 2287\n" +
        "notsubset CB 2284\n" +
        "propersubset CC 2282\n" +
        "reflexsubset CD 2286\n" +
        "element CE 2208\n" +
        "notelement CF 2209\n" +
        "angle D0 2220\n" +
        "gradient D1 2207\n" +
        "registerserif D2 F6DA\n" +
        "copyrightserif D3 F6D9\n" +
        "trademarkserif D4 F6DB\n" +
        "product D5 220F\n" +
        "radical D6 221A\n" +
        "dotmath D7 22C5\n" +
        "logicalnot D8 00AC\n" +
        "logicaland D9 2227\n" +
        "logicalor DA 2228\n" +
        "arrowdblboth DB 21D4\n" +
        "arrowdblleft DC 21D0\n" +
        "arrowdblup DD 21D1\n" +
        "arrowdblright DE 21D2\n" +
        "arrowdbldown DF 21D3\n" +
        "lozenge E0 25CA\n" +
        "angleleft E1 2329\n" +
        "registersans E2 F8E8\n" +
        "copyrightsans E3 F8E9\n" +
        "trademarksans E4 F8EA\n" +
        "summation E5 2211\n" +
        "parenlefttp E6 F8EB\n" +
        "parenleftex E7 F8EC\n" +
        "parenleftbt E8 F8ED\n" +
        "bracketlefttp E9 F8EE\n" +
        "bracketleftex EA F8EF\n" +
        "bracketleftbt EB F8F0\n" +
        "bracelefttp EC F8F1\n" +
        "braceleftmid ED F8F2\n" +
        "braceleftbt EE F8F3\n" +
        "braceex EF F8F4\n" +
        "angleright F1 232A\n" +
        "integral F2 222B\n" +
        "integraltp F3 2320\n" +
        "integralex F4 F8F5\n" +
        "integralbt F5 2321\n" +
        "parenrighttp F6 F8F6\n" +
        "parenrightex F7 F8F7\n" +
        "parenrightbt F8 F8F8\n" +
        "bracketrighttp F9 F8F9\n" +
        "bracketrightex FA F8FA\n" +
        "bracketrightbt FB F8FB\n" +
        "bracerighttp FC F8FC\n" +
        "bracerightmid FD F8FD\n" +
        "bracerightbt FE F8FE\n";
}
