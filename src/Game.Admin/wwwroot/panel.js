// The panel's only script of its own: it counts the characters of a field with data-count as they are typed. One listener on the document, so it also works
// on a page that Blazor's enhanced navigation brought in. It sends and keeps nothing.
document.addEventListener("input", function (event) {
    var counter = event.target.dataset && event.target.dataset.count;
    var output = counter && document.getElementById(counter);
    if (output) output.textContent = String(event.target.value.length);
});
