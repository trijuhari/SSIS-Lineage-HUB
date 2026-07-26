window.webFolderPicker = {
    /**
     * Opens a hidden <input type="file" webkitdirectory> dialog.
     * Returns the top-level folder name from the first file's webkitRelativePath.
     * e.g. "MyProject/subdir/pkg.dtsx" -> "MyProject"
     */
    pick: function () {
        return new Promise((resolve) => {
            const input = document.createElement('input');
            input.type = 'file';
            input.setAttribute('webkitdirectory', '');
            input.setAttribute('multiple', '');
            input.style.display = 'none';

            input.onchange = () => {
                const files = Array.from(input.files || []);
                document.body.removeChild(input);

                if (files.length === 0) {
                    resolve(null);
                    return;
                }

                // webkitRelativePath = "FolderName/sub/file.dtsx"
                // Take only the root folder name
                const firstRelative = files[0].webkitRelativePath || '';
                const topFolder = firstRelative.split('/')[0];
                resolve(topFolder || null);
            };

            input.oncancel = () => {
                document.body.removeChild(input);
                resolve(null);
            };

            document.body.appendChild(input);
            input.click();
        });
    }
};
