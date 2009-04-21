ASSEMBLY = Shoes.exe
SOURCE = Shoes.cs Options.cs
REFERENCES = -r:System.Core

all: $(ASSEMBLY)

$(ASSEMBLY): $(SOURCE)
	gmcs -out:$@ -debug $(REFERENCES) $(SOURCE)

clean:
	rm -rf $(ASSEMBLY){,.mdb}

run: $(ASSEMBLY)
	mono --debug $< -m `which mono` .

