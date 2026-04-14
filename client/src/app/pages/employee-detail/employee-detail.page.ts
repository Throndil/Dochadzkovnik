import { Component, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { EmployeeService, Employee } from '../../services/employee.service';
import { normaliseFile } from '../../utils/image-utils';

@Component({
  selector: 'app-employee-detail',
  imports: [NavbarComponent, FormsModule],
  templateUrl: './employee-detail.page.html'
})
export class EmployeeDetailPage implements OnInit {
  employee = signal<Employee | null>(null);
  firstName = '';
  lastName = '';
  phoneNumber = '';
  address = '';
  city = '';
  isActive = true;
  currentPin = signal<string | null>(null);
  showCurrentPin = signal(false);
  newPin = '';
  pinSaved = false;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);
  private id = 0;

  get pinValid() { return /^\d{4,6}$/.test(this.newPin); }

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private employeeService: EmployeeService
  ) {}

  ngOnInit() {
    this.id = Number(this.route.snapshot.paramMap.get('id'));
    this.employeeService.get(this.id).subscribe(emp => {
      this.employee.set(emp);
      this.firstName = emp.firstName;
      this.lastName = emp.lastName;
      this.phoneNumber = emp.phoneNumber ?? '';
      this.address = emp.address ?? '';
      this.city = emp.city ?? '';
      this.isActive = emp.isActive;
      this.photoPreview.set(emp.photoUrl ?? null);
      this.currentPin.set(emp.pinPlain ?? null);
    });
  }

  onSave() {
    this.employeeService.update(this.id, {
      firstName: this.firstName,
      lastName: this.lastName,
      phoneNumber: this.phoneNumber || undefined,
      address: this.address || undefined,
      city: this.city || undefined,
      isActive: this.isActive
    }).subscribe(() => this.router.navigate(['/admin/employees']));
  }

  onSetPin() {
    if (!/^\d{4,6}$/.test(this.newPin)) return;
    const savedPin = this.newPin;
    this.employeeService.setPin(this.id, savedPin).subscribe(() => {
      this.currentPin.set(savedPin);
      this.pinSaved = true;
      this.newPin = '';
      setTimeout(() => this.pinSaved = false, 3000);
    });
  }

  onFileSelected(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) this.processPhotoFile(file);
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(true);
  }

  onDragLeave() {
    this.isDragOver.set(false);
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file) this.processPhotoFile(file);
  }

  async processPhotoFile(file: File): Promise<void> {
    if (!file.type.startsWith('image/') && !file.name.match(/\.(heic|heif)$/i)) return;
    const normalised = await normaliseFile(file);   // HEIC → PNG before canvas decode
    const resized = await this.resizeImage(normalised);
    this.employeeService.uploadPhoto(this.id, resized).subscribe(url => {
      this.employee.update(e => e ? { ...e, photoUrl: url } : e);
      this.photoPreview.set(url);
    });
  }

  private resizeImage(file: File, maxDimension = 800): Promise<File> {
    return new Promise(resolve => {
      const img = new Image();
      const objectUrl = URL.createObjectURL(file);
      img.onload = () => {
        URL.revokeObjectURL(objectUrl);
        const scale = Math.min(1, maxDimension / Math.max(img.width, img.height));
        const w = Math.round(img.width * scale);
        const h = Math.round(img.height * scale);
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        canvas.getContext('2d')!.drawImage(img, 0, 0, w, h);
        canvas.toBlob(
          blob => resolve(new File([blob!], file.name.replace(/\.[^.]+$/, '.jpg'), { type: 'image/jpeg' })),
          'image/jpeg',
          0.85
        );
      };
      img.src = objectUrl;
    });
  }

  onBack() {
    this.router.navigate(['/admin/employees']);
  }
}
