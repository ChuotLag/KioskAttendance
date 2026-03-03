/** @type {import('tailwindcss').Config} */
/*module.exports = {
    content: ["./**//**.{html,js}"],
    safelist: [
        { pattern: /(bg|text|border)-\[#([0-9a-fA-F]{3,8})\]/ },
        { pattern: /(rounded|p|px|py|m|mx|my|w|h)-\[[^\]]+\]/ },
        { pattern: /(grid-cols|col-span|row-span|gap)-\d+/ },
    ],
    theme: { extend: {} },
    plugins: [],
}; */
module.exports = {
    content: [
        "../www/index.html",
        "../www/pages/**/*.html",
        "../www/assets/js/**/*.js",
        "./assets/js/**/*.js"
    ],
    theme: { extend: {} },
    plugins: []
};

