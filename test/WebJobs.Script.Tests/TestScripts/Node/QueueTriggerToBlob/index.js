﻿module.exports = function (context, input) {
    var json = JSON.stringify(input);
    context.log('Node.js script processed queue message', json);

    context.bindings.output = json;

    context.done();
}