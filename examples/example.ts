type Foo = {x: number, y: string};

function x(foo: Foo): void {
    console.log(foo.y.replace("x", "y"));
}

const obj = { x: 3, y: "Hello World" }

function random(): string {
    return "y";
}

obj[random()] = null;
x(obj);