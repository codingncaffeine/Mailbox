namespace Mailbox.Junk;

/// <summary>
/// The choices the International tab offers: the country-code top-level domains, and the
/// character sets a message can be written in.
/// </summary>
/// <remarks>
/// Data, in the library rather than the dialog, so a test can read it and a rule could one day.
/// The domain list is the ISO 3166 country codes with the name each is known by; the encodings
/// are the ones mail is actually written in — the ISO 8859 family, the Windows code pages, the
/// East Asian sets and the Unicode forms.
/// </remarks>
public static class JunkLists
{
    /// <summary>One country-code top-level domain and its country or region.</summary>
    public sealed record TopLevelDomain(string Code, string Country);

    /// <summary>One character set: what the dialog shows, and the charset name a message declares.</summary>
    public sealed record Encoding(string Label, string Charset);

    public static IReadOnlyList<TopLevelDomain> TopLevelDomains { get; } =
    [
        new("ac", "Ascension Island"), new("ad", "Andorra"), new("ae", "United Arab Emirates"),
        new("af", "Afghanistan"), new("ag", "Antigua and Barbuda"), new("ai", "Anguilla"),
        new("al", "Albania"), new("am", "Armenia"), new("ao", "Angola"), new("aq", "Antarctica"),
        new("ar", "Argentina"), new("as", "American Samoa"), new("at", "Austria"),
        new("au", "Australia"), new("aw", "Aruba"), new("ax", "Åland Islands"), new("az", "Azerbaijan"),
        new("ba", "Bosnia and Herzegovina"), new("bb", "Barbados"), new("bd", "Bangladesh"),
        new("be", "Belgium"), new("bf", "Burkina Faso"), new("bg", "Bulgaria"), new("bh", "Bahrain"),
        new("bi", "Burundi"), new("bj", "Benin"), new("bm", "Bermuda"), new("bn", "Brunei"),
        new("bo", "Bolivia"), new("br", "Brazil"), new("bs", "Bahamas"), new("bt", "Bhutan"),
        new("bw", "Botswana"), new("by", "Belarus"), new("bz", "Belize"),
        new("ca", "Canada"), new("cc", "Cocos (Keeling) Islands"), new("cd", "Congo, Democratic Republic of the"),
        new("cf", "Central African Republic"), new("cg", "Congo"), new("ch", "Switzerland"),
        new("ci", "Côte d'Ivoire"), new("ck", "Cook Islands"), new("cl", "Chile"), new("cm", "Cameroon"),
        new("cn", "China"), new("co", "Colombia"), new("cr", "Costa Rica"), new("cu", "Cuba"),
        new("cv", "Cabo Verde"), new("cw", "Curaçao"), new("cx", "Christmas Island"), new("cy", "Cyprus"),
        new("cz", "Czechia"),
        new("de", "Germany"), new("dj", "Djibouti"), new("dk", "Denmark"), new("dm", "Dominica"),
        new("do", "Dominican Republic"), new("dz", "Algeria"),
        new("ec", "Ecuador"), new("ee", "Estonia"), new("eg", "Egypt"), new("er", "Eritrea"),
        new("es", "Spain"), new("et", "Ethiopia"), new("eu", "European Union"),
        new("fi", "Finland"), new("fj", "Fiji"), new("fk", "Falkland Islands"), new("fm", "Micronesia"),
        new("fo", "Faroe Islands"), new("fr", "France"),
        new("ga", "Gabon"), new("gd", "Grenada"), new("ge", "Georgia"), new("gf", "French Guiana"),
        new("gg", "Guernsey"), new("gh", "Ghana"), new("gi", "Gibraltar"), new("gl", "Greenland"),
        new("gm", "Gambia"), new("gn", "Guinea"), new("gp", "Guadeloupe"), new("gq", "Equatorial Guinea"),
        new("gr", "Greece"), new("gs", "South Georgia and the South Sandwich Islands"), new("gt", "Guatemala"),
        new("gu", "Guam"), new("gw", "Guinea-Bissau"), new("gy", "Guyana"),
        new("hk", "Hong Kong"), new("hm", "Heard Island and McDonald Islands"), new("hn", "Honduras"),
        new("hr", "Croatia"), new("ht", "Haiti"), new("hu", "Hungary"),
        new("id", "Indonesia"), new("ie", "Ireland"), new("il", "Israel"), new("im", "Isle of Man"),
        new("in", "India"), new("io", "British Indian Ocean Territory"), new("iq", "Iraq"), new("ir", "Iran"),
        new("is", "Iceland"), new("it", "Italy"),
        new("je", "Jersey"), new("jm", "Jamaica"), new("jo", "Jordan"), new("jp", "Japan"),
        new("ke", "Kenya"), new("kg", "Kyrgyzstan"), new("kh", "Cambodia"), new("ki", "Kiribati"),
        new("km", "Comoros"), new("kn", "Saint Kitts and Nevis"), new("kp", "Korea, Democratic People's Republic of"),
        new("kr", "Korea, Republic of"), new("kw", "Kuwait"), new("ky", "Cayman Islands"), new("kz", "Kazakhstan"),
        new("la", "Laos"), new("lb", "Lebanon"), new("lc", "Saint Lucia"), new("li", "Liechtenstein"),
        new("lk", "Sri Lanka"), new("lr", "Liberia"), new("ls", "Lesotho"), new("lt", "Lithuania"),
        new("lu", "Luxembourg"), new("lv", "Latvia"), new("ly", "Libya"),
        new("ma", "Morocco"), new("mc", "Monaco"), new("md", "Moldova"), new("me", "Montenegro"),
        new("mg", "Madagascar"), new("mh", "Marshall Islands"), new("mk", "North Macedonia"), new("ml", "Mali"),
        new("mm", "Myanmar"), new("mn", "Mongolia"), new("mo", "Macao"), new("mp", "Northern Mariana Islands"),
        new("mq", "Martinique"), new("mr", "Mauritania"), new("ms", "Montserrat"), new("mt", "Malta"),
        new("mu", "Mauritius"), new("mv", "Maldives"), new("mw", "Malawi"), new("mx", "Mexico"),
        new("my", "Malaysia"), new("mz", "Mozambique"),
        new("na", "Namibia"), new("nc", "New Caledonia"), new("ne", "Niger"), new("nf", "Norfolk Island"),
        new("ng", "Nigeria"), new("ni", "Nicaragua"), new("nl", "Netherlands"), new("no", "Norway"),
        new("np", "Nepal"), new("nr", "Nauru"), new("nu", "Niue"), new("nz", "New Zealand"),
        new("om", "Oman"),
        new("pa", "Panama"), new("pe", "Peru"), new("pf", "French Polynesia"), new("pg", "Papua New Guinea"),
        new("ph", "Philippines"), new("pk", "Pakistan"), new("pl", "Poland"), new("pm", "Saint Pierre and Miquelon"),
        new("pn", "Pitcairn"), new("pr", "Puerto Rico"), new("ps", "Palestine"), new("pt", "Portugal"),
        new("pw", "Palau"), new("py", "Paraguay"),
        new("qa", "Qatar"),
        new("re", "Réunion"), new("ro", "Romania"), new("rs", "Serbia"), new("ru", "Russia"), new("rw", "Rwanda"),
        new("sa", "Saudi Arabia"), new("sb", "Solomon Islands"), new("sc", "Seychelles"), new("sd", "Sudan"),
        new("se", "Sweden"), new("sg", "Singapore"), new("sh", "Saint Helena"), new("si", "Slovenia"),
        new("sk", "Slovakia"), new("sl", "Sierra Leone"), new("sm", "San Marino"), new("sn", "Senegal"),
        new("so", "Somalia"), new("sr", "Suriname"), new("ss", "South Sudan"), new("st", "Sao Tome and Principe"),
        new("su", "Soviet Union (historical)"), new("sv", "El Salvador"), new("sx", "Sint Maarten"),
        new("sy", "Syria"), new("sz", "Eswatini"),
        new("tc", "Turks and Caicos Islands"), new("td", "Chad"), new("tf", "French Southern Territories"),
        new("tg", "Togo"), new("th", "Thailand"), new("tj", "Tajikistan"), new("tk", "Tokelau"),
        new("tl", "Timor-Leste"), new("tm", "Turkmenistan"), new("tn", "Tunisia"), new("to", "Tonga"),
        new("tr", "Türkiye"), new("tt", "Trinidad and Tobago"), new("tv", "Tuvalu"), new("tw", "Taiwan"),
        new("tz", "Tanzania"),
        new("ua", "Ukraine"), new("ug", "Uganda"), new("uk", "United Kingdom"), new("us", "United States"),
        new("uy", "Uruguay"), new("uz", "Uzbekistan"),
        new("va", "Holy See"), new("vc", "Saint Vincent and the Grenadines"), new("ve", "Venezuela"),
        new("vg", "Virgin Islands, British"), new("vi", "Virgin Islands, U.S."), new("vn", "Viet Nam"),
        new("vu", "Vanuatu"),
        new("wf", "Wallis and Futuna"), new("ws", "Samoa"),
        new("ye", "Yemen"), new("yt", "Mayotte"),
        new("za", "South Africa"), new("zm", "Zambia"), new("zw", "Zimbabwe"),
    ];

    public static IReadOnlyList<Encoding> Encodings { get; } =
    [
        new("Arabic (ISO)", "iso-8859-6"), new("Arabic (Windows)", "windows-1256"),
        new("Baltic (ISO)", "iso-8859-4"), new("Baltic (Windows)", "windows-1257"),
        new("Central European (ISO)", "iso-8859-2"), new("Central European (Windows)", "windows-1250"),
        new("Chinese Simplified (GB2312)", "gb2312"), new("Chinese Simplified (GB18030)", "gb18030"),
        new("Chinese Simplified (HZ)", "hz-gb-2312"), new("Chinese Traditional (Big5)", "big5"),
        new("Cyrillic (ISO)", "iso-8859-5"), new("Cyrillic (KOI8-R)", "koi8-r"),
        new("Cyrillic (KOI8-U)", "koi8-u"), new("Cyrillic (Windows)", "windows-1251"),
        new("Greek (ISO)", "iso-8859-7"), new("Greek (Windows)", "windows-1253"),
        new("Hebrew (ISO-Logical)", "iso-8859-8-i"), new("Hebrew (ISO-Visual)", "iso-8859-8"),
        new("Hebrew (Windows)", "windows-1255"),
        new("Japanese (EUC)", "euc-jp"), new("Japanese (JIS)", "iso-2022-jp"), new("Japanese (Shift-JIS)", "shift_jis"),
        new("Korean (EUC)", "euc-kr"), new("Korean (ISO)", "iso-2022-kr"),
        new("Latin 3 (ISO)", "iso-8859-3"), new("Latin 9 (ISO)", "iso-8859-15"),
        new("Thai (Windows)", "windows-874"),
        new("Turkish (ISO)", "iso-8859-9"), new("Turkish (Windows)", "windows-1254"),
        new("Unicode (UTF-7)", "utf-7"), new("Unicode (UTF-8)", "utf-8"), new("Unicode (UTF-16)", "utf-16"),
        new("Vietnamese (Windows)", "windows-1258"),
        new("Western European (ISO)", "iso-8859-1"), new("Western European (Windows)", "windows-1252"),
    ];
}
